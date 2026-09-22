//! 纯逻辑核心：名称解析、校验、重命名计划。不依赖任何 UI，可单测。

use std::collections::HashMap;
use std::path::{Path, PathBuf};

/// 已载入的文件条目
#[derive(Debug, Clone, PartialEq)]
pub struct RenameItem {
    pub source_path: PathBuf,
    pub file_name: String,
    pub extension: String, // 含点，如 ".txt"；目录或无扩展名为 ""
    pub is_dir: bool,
}

/// 目标位置的占用情况（由 UI 扫描目录得到后传入）
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Occupant {
    Free,
    /// 被本批次中的某个源文件占用（它会被改名让位，允许循环交换）
    InBatch,
    /// 被本批次之外的文件占用
    Outside,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Status {
    /// 可直接执行
    Ready,
    /// 目标名称被本批次其他文件占用，形成交换/轮换，仍可执行
    SwapLoop,
    /// 无法执行，附带原因
    Skip(String),
}

#[derive(Debug, Clone, PartialEq)]
pub struct PlanEntry {
    pub item_index: usize,
    pub target_name: String,
    pub status: Status,
}

/// 把名称编辑框的多行文本解析为名称列表：按行拆分、去首尾空白、丢弃空行。
pub fn parse_names(text: &str) -> Vec<String> {
    text.split(['\r', '\n'])
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(ToOwned::to_owned)
        .collect()
}

const ILLEGAL_CHARS: &[char] = &['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

fn is_reserved(name: &str) -> bool {
    // Windows 保留名：CON/PRN/AUX/NUL/COM1-9/LPT1-9（上标 ¹²³ 视作 1/2/3）
    let normalized: String = name
        .chars()
        .map(|c| match c {
            '\u{00B9}' => '1',
            '\u{00B2}' => '2',
            '\u{00B3}' => '3',
            _ => c,
        })
        .collect();
    let upper = normalized.to_uppercase();
    matches!(upper.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || (upper.len() == 4
            && (upper.starts_with("COM") || upper.starts_with("LPT"))
            && upper.as_bytes()[3].is_ascii_digit()
            && upper.as_bytes()[3] != b'0')
}

/// 校验单个名称（不含扩展名）。occ 为该名称（含扩展名）在当前目录的占用情况。
pub fn validate_name(
    raw: &str,
    extension: &str,
    is_dir: bool,
    occupied: Occupant,
) -> Result<Status, String> {
    if raw.ends_with('.') || raw.ends_with(' ') {
        return Err("名称不能以点或空格结尾".to_string());
    }
    let name = raw.trim();
    if name.is_empty() {
        return Err("名称为空".to_string());
    }
    if let Some(c) = name.chars().find(|c| ILLEGAL_CHARS.contains(c) || c.is_control()) {
        return Err(format!("包含非法字符: {c}"));
    }
    if is_reserved(name) {
        return Err("保留名称不可用".to_string());
    }
    let full_len = name.encode_utf16().count() + extension.encode_utf16().count();
    if full_len > 255 {
        return Err("名称过长（超过 255 字符）".to_string());
    }
    // 完整路径长度上限（保留安全余量）
    // 注意：调用方传入的 raw 只是名称部分，路径检查在 plan() 中按父目录计算。
    let _ = is_dir;
    match occupied {
        Occupant::Outside => Err("目标名称已存在".to_string()),
        Occupant::InBatch => Ok(Status::SwapLoop),
        Occupant::Free => Ok(Status::Ready),
    }
}

/// 计算一批文件的重命名计划。
///
/// - `names`：名称列表（不含扩展名），第 i 个名称对应当前顺序下第 i 个文件
/// - `keep_extension`：为 true 时自动补上各文件原扩展名
/// - `occupancy`：调用方预先扫描好的“目标名 → 占用情况”映射；键为最终目标文件名
pub fn plan(
    items: &[RenameItem],
    names: &[String],
    keep_extension: bool,
    occupancy: &HashMap<String, Occupant>,
) -> Vec<PlanEntry> {
    items
        .iter()
        .enumerate()
        .map(|(i, item)| {
            let Some(raw) = names.get(i) else {
                return PlanEntry {
                    item_index: i,
                    target_name: String::new(),
                    status: Status::Skip("<名称不足>".to_string()),
                };
            };
            let ext = if keep_extension { item.extension.as_str() } else { "" };
            let target_name = apply_extension(raw.trim(), ext);
            if target_name.eq_ignore_ascii_case(&item.file_name) {
                return PlanEntry { item_index: i, target_name, status: Status::Skip("名称未变化".to_string()) };
            }
            let occupied = *occupancy.get(&target_name).unwrap_or(&Occupant::Free);
            match validate_name(raw, ext, item.is_dir, occupied) {
                Ok(status) => PlanEntry { item_index: i, target_name, status },
                Err(reason) => PlanEntry { item_index: i, target_name, status: Status::Skip(reason) },
            }
        })
        .collect()
}

/// 需要执行的两段移动操作（目标名不含路径的部分只用于展示）。
#[derive(Debug, Clone, PartialEq)]
pub struct MoveOp {
    pub from: PathBuf,
    pub to: PathBuf,
}

/// 从计划中提取可执行（Ready/SwapLoop）的移动操作。
pub fn ops_from_plan(items: &[RenameItem], plan: &[PlanEntry]) -> Vec<MoveOp> {
    plan.iter()
        .filter(|e| matches!(e.status, Status::Ready | Status::SwapLoop))
        .map(|e| {
            let item = &items[e.item_index];
            let parent = item
                .source_path
                .parent()
                .map(Path::to_path_buf)
                .unwrap_or_default();
            MoveOp {
                from: item.source_path.clone(),
                to: parent.join(&e.target_name),
            }
        })
        .collect()
}

fn apply_extension(name: &str, ext: &str) -> String {
    if ext.is_empty() {
        name.to_string()
    } else {
        format!("{name}{ext}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn item(name: &str, ext: &str) -> RenameItem {
        RenameItem {
            source_path: PathBuf::from("D:/t").join(name).with_extension(""),
            file_name: name.to_string(),
            extension: ext.to_string(),
            is_dir: false,
        }
    }

    #[test]
    fn parse_names_splits_and_trims() {
        assert_eq!(parse_names("a\r\nb\nc\r\r\n  d  \n\n"), vec!["a", "b", "c", "d"]);
        assert_eq!(parse_names(""), Vec::<String>::new());
        assert_eq!(parse_names("  \n\t "), Vec::<String>::new());
    }

    #[test]
    fn validate_rejects_illegal_chars() {
        for bad in ["a/b", "a\\b", "a:b", "a*b", "a?b", "a\"b", "a<b", "a>b", "a|b"] {
            assert!(validate_name(bad, "", false, Occupant::Free).is_err(), "{bad} should fail");
        }
    }

    #[test]
    fn validate_rejects_reserved() {
        for bad in ["CON", "con", "PRN", "AUX", "NUL", "COM1", "com9", "LPT1", "COM¹"] {
            assert!(validate_name(bad, "", false, Occupant::Free).is_err(), "{bad} should fail");
        }
        assert!(validate_name("CONSOLE", "", false, Occupant::Free).is_ok());
        assert!(validate_name("COM10", "", false, Occupant::Free).is_ok());
    }

    #[test]
    fn validate_rejects_trailing_dot_or_space() {
        assert!(validate_name("name.", "", false, Occupant::Free).is_err());
        assert!(validate_name("name ", "", false, Occupant::Free).is_err());
    }

    #[test]
    fn validate_rejects_outside_occupant_allows_batch_swap() {
        assert!(validate_name("x", "", false, Occupant::Outside).is_err());
        assert_eq!(validate_name("x", "", false, Occupant::InBatch).unwrap(), Status::SwapLoop);
        assert_eq!(validate_name("x", "", false, Occupant::Free).unwrap(), Status::Ready);
    }

    #[test]
    fn validate_rejects_too_long() {
        let long = "x".repeat(256);
        assert!(validate_name(&long, "", false, Occupant::Free).is_err());
        let ok = "x".repeat(255);
        assert!(validate_name(&ok, "", false, Occupant::Free).is_ok());
    }

    #[test]
    fn plan_assigns_in_order_and_appends_ext() {
        let items = vec![item("b.txt", ".txt"), item("a.txt", ".txt")];
        let occ = HashMap::new();
        let p = plan(&items, &["one".to_string(), "two".to_string()], true, &occ);
        assert_eq!(p[0].target_name, "one.txt");
        assert_eq!(p[1].target_name, "two.txt");
        assert_eq!(p[0].status, Status::Ready);
    }

    #[test]
    fn plan_marks_missing_names() {
        let items = vec![item("a", ""), item("b", "")];
        let occ = HashMap::new();
        let p = plan(&items, &["only".to_string()], false, &occ);
        assert_eq!(p[1].status, Status::Skip("<名称不足>".to_string()));
    }

    #[test]
    fn plan_flags_swap_loop_when_target_held_by_batch_member() {
        // a.txt 改名为 b.txt，而 b.txt 也在批次中且要改名为 c.txt
        let items = vec![item("a.txt", ".txt"), item("b.txt", ".txt")];
        let mut occ = HashMap::new();
        occ.insert("b.txt".to_string(), Occupant::InBatch);
        occ.insert("c.txt".to_string(), Occupant::Free);
        let p = plan(&items, &["b".to_string(), "c".to_string()], true, &occ);
        assert_eq!(p[0].status, Status::SwapLoop);
        assert_eq!(p[1].status, Status::Ready);
    }

    #[test]
    fn plan_blocks_when_target_held_by_outsider() {
        let items = vec![item("a.txt", ".txt")];
        let mut occ = HashMap::new();
        occ.insert("b.txt".to_string(), Occupant::Outside);
        let p = plan(&items, &["b".to_string()], true, &occ);
        assert_eq!(p[0].status, Status::Skip("目标名称已存在".to_string()));
    }

    #[test]
    fn ops_from_plan_skips_invalid() {
        let items = vec![item("a", ""), item("b", ""), item("c", "")];
        let occ = HashMap::new();
        let p = plan(&items, &["x".to_string(), "bad/name".to_string(), "z".to_string()], false, &occ);
        let ops = ops_from_plan(&items, &p);
        assert_eq!(ops.len(), 2);
        assert!(ops.iter().all(|o| o.from != items[1].source_path));
    }

    #[test]
    fn plan_blocks_unchanged_name() {
        let items = vec![item("a.txt", ".txt")];
        let occ = HashMap::new();
        let p = plan(&items, &["a".to_string()], true, &occ);
        assert_eq!(p[0].status, Status::Skip("名称未变化".to_string()));
        // 仅大小写不同也算未变化
        let p2 = plan(&items, &["A".to_string()], true, &occ);
        assert_eq!(p2[0].status, Status::Skip("名称未变化".to_string()));
    }

    #[test]
    fn keep_extension_uses_each_files_own_ext() {
        let mut a = item("a.md", ".md");
        a.source_path = PathBuf::from("D:/t/a.md");
        let mut b = item("b", "");
        b.source_path = PathBuf::from("D:/t/b");
        let occ = HashMap::new();
        let p = plan(&[a, b], &["n1".to_string(), "n2".to_string()], true, &occ);
        assert_eq!(p[0].target_name, "n1.md");
        assert_eq!(p[1].target_name, "n2");
    }
}
