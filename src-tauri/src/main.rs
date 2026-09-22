//! 行序 · LineRename — Tauri 版入口。
//! 纯逻辑在 core，执行器在 executor；本文件只做状态管理与命令桥接。
// release 构建隐藏控制台窗口（debug 保留，便于看日志）
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod core;
mod executor;

use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use serde::Serialize;
use tauri::State;

use core::{parse_names, plan, ops_from_plan, MoveOp, Occupant, PlanEntry, RenameItem, Status};

struct AppState {
    items: Mutex<Vec<RenameItem>>,
}

#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
struct RowDto {
    index: usize,
    source_name: String,
    is_dir: bool,
    target_name: String,
    status_kind: String,
    status_text: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct RenameOutcome {
    moved: usize,
    skipped: Vec<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SortOutcome {
    changed: usize,
    total: usize,
    descending: bool,
}

fn ext_of(path: &Path) -> String {
    match path.extension().and_then(|e| e.to_str()) {
        Some(e) if !e.is_empty() => format!(".{e}"),
        _ => String::new(),
    }
}

fn status_parts(status: &Status) -> (&'static str, String) {
    match status {
        Status::Ready => ("ready", "就绪".to_string()),
        Status::SwapLoop => ("swap", "交换".to_string()),
        Status::Skip(reason) => ("skip", reason.clone()),
    }
}

fn make_rows(items: &[RenameItem], plan_entries: Option<&[PlanEntry]>) -> Vec<RowDto> {
    items
        .iter()
        .enumerate()
        .map(|(i, it)| match plan_entries.and_then(|p| p.get(i)) {
            Some(e) => {
                let (kind, text) = status_parts(&e.status);
                RowDto {
                    index: i,
                    source_name: it.file_name.clone(),
                    is_dir: it.is_dir,
                    target_name: e.target_name.clone(),
                    status_kind: kind.to_string(),
                    status_text: text,
                }
            }
            None => RowDto {
                index: i,
                source_name: it.file_name.clone(),
                is_dir: it.is_dir,
                target_name: String::new(),
                status_kind: "none".to_string(),
                status_text: String::new(),
            },
        })
        .collect()
}

/// 扫描相关目录，得出每个候选目标名的占用情况（Windows 文件系统大小写不敏感）。
fn build_occupancy(items: &[RenameItem], names: &[String], keep_ext: bool) -> HashMap<String, Occupant> {
    let mut dirs: HashMap<PathBuf, Vec<String>> = HashMap::new();
    for (i, item) in items.iter().enumerate() {
        let Some(raw) = names.get(i) else { continue };
        if raw.trim().is_empty() {
            continue;
        }
        let ext = if keep_ext { item.extension.as_str() } else { "" };
        let target = format!("{}{}", raw.trim(), ext);
        let parent = item.source_path.parent().unwrap_or_else(|| Path::new("")).to_path_buf();
        dirs.entry(parent).or_default().push(target);
    }

    let mut occ = HashMap::new();
    for (dir, targets) in dirs {
        let entries: Vec<String> = fs::read_dir(&dir)
            .map(|rd| {
                rd.filter_map(|e| e.ok())
                    .map(|e| e.file_name().to_string_lossy().into_owned())
                    .collect()
            })
            .unwrap_or_default();
        for target in targets {
            if occ.contains_key(&target) {
                continue;
            }
            let exists = entries.iter().any(|e| e.eq_ignore_ascii_case(&target));
            let held_by_batch = items.iter().any(|it| {
                it.source_path.parent().unwrap_or_else(|| Path::new("")) == dir
                    && it.file_name.eq_ignore_ascii_case(&target)
            });
            let v = if !exists {
                Occupant::Free
            } else if held_by_batch {
                Occupant::InBatch
            } else {
                Occupant::Outside
            };
            occ.insert(target, v);
        }
    }
    occ
}

enum Tok {
    Text(String),
    Num(String),
}

fn tokenize(s: &str) -> Vec<Tok> {
    let mut v = Vec::new();
    let mut text = String::new();
    let mut digits: Option<String> = None;
    for c in s.chars() {
        if c.is_ascii_digit() {
            if digits.is_none() {
                if !text.is_empty() {
                    v.push(Tok::Text(std::mem::take(&mut text)));
                }
                digits = Some(String::new());
            }
            digits.as_mut().unwrap().push(c);
        } else {
            if let Some(d) = digits.take() {
                v.push(Tok::Num(d));
            }
            text.push(c);
        }
    }
    if let Some(d) = digits.take() {
        v.push(Tok::Num(d));
    }
    if !text.is_empty() {
        v.push(Tok::Text(text));
    }
    v
}

fn natural_cmp(a: &str, b: &str) -> std::cmp::Ordering {
    use std::cmp::Ordering;
    let ta = tokenize(a);
    let tb = tokenize(b);
    for i in 0..ta.len().min(tb.len()) {
        let ord = match (&ta[i], &tb[i]) {
            (Tok::Num(x), Tok::Num(y)) => {
                let (xn, yn) = (x.trim_start_matches('0'), y.trim_start_matches('0'));
                let by_val = xn.len().cmp(&yn.len()).then_with(|| xn.cmp(yn));
                by_val.then_with(|| x.len().cmp(&y.len()))
            }
            (Tok::Text(x), Tok::Text(y)) => x.cmp(y),
            (Tok::Num(_), Tok::Text(_)) => Ordering::Less,
            (Tok::Text(_), Tok::Num(_)) => Ordering::Greater,
        };
        if ord != Ordering::Equal {
            return ord;
        }
    }
    ta.len().cmp(&tb.len()).then_with(|| a.cmp(b))
}

fn group_by_dir(ops: Vec<MoveOp>) -> Vec<(PathBuf, Vec<MoveOp>)> {
    let mut groups: Vec<(PathBuf, Vec<MoveOp>)> = Vec::new();
    for op in ops {
        let dir = op.from.parent().map(Path::to_path_buf).unwrap_or_default();
        match groups.iter_mut().find(|(d, _)| *d == dir) {
            Some((_, g)) => g.push(op),
            None => groups.push((dir, vec![op])),
        }
    }
    groups
}

/// 执行一批移动（按目录分组），返回成功的移动；失败时尽力回滚该组。
fn exec_grouped(ops: Vec<MoveOp>) -> Result<Vec<MoveOp>, String> {
    let mut moved = Vec::new();
    for (_dir, group) in group_by_dir(ops) {
        match executor::execute(&group, None) {
            Ok(res) => moved.extend(res.moved),
            Err(e) => {
                let done = moved.len();
                if done > 0 {
                    let _ = executor::write_receipt(&moved);
                }
                return Err(format!("重命名失败（已完成 {done} 项）: {e}"));
            }
        }
    }
    Ok(moved)
}

fn apply_moves_to_items(items: &mut Vec<RenameItem>, moved: &[MoveOp]) {
    for m in moved {
        if let Some(it) = items.iter_mut().find(|it| it.source_path == m.from) {
            it.source_path = m.to.clone();
            it.file_name = m
                .to
                .file_name()
                .map(|n| n.to_string_lossy().into_owned())
                .unwrap_or_default();
            it.extension = if it.is_dir { String::new() } else { ext_of(&m.to) };
        }
    }
}

#[tauri::command]
fn pick_files() -> Vec<String> {
    rfd::FileDialog::new()
        .set_title("选择文件（可多选）")
        .pick_files()
        .unwrap_or_default()
        .iter()
        .map(|p| p.to_string_lossy().into_owned())
        .collect()
}

/// 列表本为自然序（或为空）时整体重排；已被手动调整过时，新文件按自然序追加到末尾。
fn add_and_merge(items: &mut Vec<RenameItem>, added: Vec<RenameItem>) {
    let natural = items
        .windows(2)
        .all(|w| natural_cmp(&w[0].file_name, &w[1].file_name) != std::cmp::Ordering::Greater);
    let old_len = items.len();
    items.extend(added);
    if natural {
        items.sort_by(|a, b| natural_cmp(&a.file_name, &b.file_name));
    } else {
        items[old_len..].sort_by(|a, b| natural_cmp(&a.file_name, &b.file_name));
    }
}

#[tauri::command]
fn add_paths(state: State<AppState>, paths: Vec<String>) -> Result<(), String> {
    let mut items = state.items.lock().map_err(|e| e.to_string())?;
    let mut added = Vec::new();
    for p in paths {
        let path = PathBuf::from(&p);
        let md = fs::metadata(&path).map_err(|e| format!("无法读取 {}: {e}", path.display()))?;
        let file_name = path
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .ok_or_else(|| format!("路径无效: {p}"))?;
        if items.iter().any(|it| it.source_path == path) {
            continue;
        }
        let is_dir = md.is_dir();
        let extension = if is_dir { String::new() } else { ext_of(&path) };
        added.push(RenameItem {
            source_path: path,
            file_name,
            extension,
            is_dir,
        });
    }
    add_and_merge(&mut items, added);
    Ok(())
}

#[tauri::command]
fn sort_items(state: State<AppState>, descending: bool) -> Result<SortOutcome, String> {
    let mut items = state.items.lock().map_err(|e| e.to_string())?;
    let before: Vec<PathBuf> = items.iter().map(|i| i.source_path.clone()).collect();
    items.sort_by(|a, b| {
        let ord = natural_cmp(&a.file_name, &b.file_name);
        if descending { ord.reverse() } else { ord }
    });
    let changed = items
        .iter()
        .zip(&before)
        .filter(|(a, b)| a.source_path != **b)
        .count();
    Ok(SortOutcome { changed, total: items.len(), descending })
}

#[tauri::command]
fn move_item(state: State<AppState>, index: usize, delta: i32) -> Result<(), String> {
    let mut items = state.items.lock().map_err(|e| e.to_string())?;
    let to = index as i32 + delta;
    if index >= items.len() || to < 0 || to as usize >= items.len() {
        return Ok(());
    }
    items.swap(index, to as usize);
    Ok(())
}

#[tauri::command]
fn clear_items(state: State<AppState>) -> Result<(), String> {
    let mut items = state.items.lock().map_err(|e| e.to_string())?;
    items.clear();
    Ok(())
}

#[tauri::command]
fn preview(state: State<AppState>, names: String, keep_ext: bool) -> Result<Vec<RowDto>, String> {
    let items = state.items.lock().map_err(|e| e.to_string())?;
    if items.is_empty() {
        return Ok(Vec::new());
    }
    let names = parse_names(&names);
    let occ = build_occupancy(&items, &names, keep_ext);
    let plan_entries = plan(&items, &names, keep_ext, &occ);
    Ok(make_rows(&items, Some(&plan_entries)))
}

#[tauri::command]
fn rename(state: State<AppState>, names: String, keep_ext: bool) -> Result<RenameOutcome, String> {
    let items = state.items.lock().map_err(|e| e.to_string())?;
    if items.is_empty() {
        return Err("文件列表为空".to_string());
    }
    let names = parse_names(&names);
    let occ = build_occupancy(&items, &names, keep_ext);
    let plan_entries = plan(&items, &names, keep_ext, &occ);
    let ops = ops_from_plan(&items, &plan_entries);
    let skipped: Vec<String> = plan_entries
        .iter()
        .filter_map(|e| match &e.status {
            Status::Skip(r) => Some(format!("{}（{r}）", items[e.item_index].file_name)),
            _ => None,
        })
        .collect();
    if ops.is_empty() {
        return Err("没有可执行的重命名".to_string());
    }
    let moved = exec_grouped(ops)?;
    executor::write_receipt(&moved).map_err(|e| format!("重命名成功但无法保存撤销凭据: {e}"))?;
    drop(items);
    let mut items = state.items.lock().map_err(|e| e.to_string())?;
    apply_moves_to_items(&mut items, &moved);
    Ok(RenameOutcome { moved: moved.len(), skipped })
}

#[tauri::command]
fn undo(state: State<AppState>) -> Result<String, String> {
    let ops = executor::read_receipt().map_err(|_| "没有可撤销的操作".to_string())?;
    if ops.is_empty() {
        return Err("没有可撤销的操作".to_string());
    }
    let moved = exec_grouped(ops)?;
    executor::write_receipt(&moved).map_err(|e| format!("已恢复但无法保存凭据: {e}"))?;
    let mut items = state.items.lock().map_err(|e| e.to_string())?;
    apply_moves_to_items(&mut items, &moved);
    Ok(format!("已恢复 {} 项", moved.len()))
}

fn main() {
    tauri::Builder::default()
        .manage(AppState { items: Mutex::new(Vec::new()) })
        .invoke_handler(tauri::generate_handler![
            pick_files,
            add_paths,
            sort_items,
            move_item,
            clear_items,
            preview,
            rename,
            undo
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cmp::Ordering;

    #[test]
    fn add_and_merge_sorts_when_natural_and_appends_when_manual() {
        let mk = |n: &str| RenameItem {
            source_path: PathBuf::from(n),
            file_name: n.to_string(),
            extension: String::new(),
            is_dir: false,
        };
        // 空列表：添加后整体自然排序
        let mut items: Vec<RenameItem> = Vec::new();
        add_and_merge(&mut items, vec![mk("10.txt"), mk("2.txt"), mk("1.txt")]);
        let names: Vec<&str> = items.iter().map(|i| i.file_name.as_str()).collect();
        assert_eq!(names, vec!["1.txt", "2.txt", "10.txt"]);

        // 自然序列表：新文件合入后整体保持自然序
        add_and_merge(&mut items, vec![mk("9.txt")]);
        let names: Vec<&str> = items.iter().map(|i| i.file_name.as_str()).collect();
        assert_eq!(names, vec!["1.txt", "2.txt", "9.txt", "10.txt"]);

        // 手动顺序（非自然序）：保留原顺序，新文件按自然序追加尾部
        let mut manual = vec![mk("b.txt"), mk("a.txt")];
        add_and_merge(&mut manual, vec![mk("z.txt"), mk("10.txt"), mk("2.txt")]);
        let names: Vec<&str> = manual.iter().map(|i| i.file_name.as_str()).collect();
        assert_eq!(names, vec!["b.txt", "a.txt", "2.txt", "10.txt", "z.txt"]);
    }

    #[test]
    fn natural_sort_orders_digit_runs() {
        let mut v = vec!["10.txt", "2.txt", "1.txt", "a10", "a2", "a1"];
        v.sort_by(|a, b| natural_cmp(a, b));
        assert_eq!(v, vec!["1.txt", "2.txt", "10.txt", "a1", "a2", "a10"]);
    }

    #[test]
    fn natural_sort_handles_leading_zeros() {
        assert_eq!(natural_cmp("01", "1"), Ordering::Greater); // 前导零多者排后
        assert_eq!(natural_cmp("001", "01"), Ordering::Greater);
        assert_eq!(natural_cmp("abc", "2"), Ordering::Greater); // 数字段先于文本段
    }

    #[test]
    fn ext_of_prepends_dot_and_handles_none() {
        assert_eq!(ext_of(Path::new("D:/t/a.txt")), ".txt");
        assert_eq!(ext_of(Path::new("D:/t/a")), "");
        assert_eq!(ext_of(Path::new("D:/t/.git")), ""); // 点开头不算扩展名
        assert_eq!(ext_of(Path::new("D:/t/a.tar.gz")), ".gz");
    }

    #[test]
    fn occupancy_marks_outsider_in_batch_and_free() {
        let dir = std::env::temp_dir().join(format!("linerename-occ-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("a.txt"), b"a").unwrap();
        fs::write(dir.join("b.txt"), b"b").unwrap();
        fs::write(dir.join("taken.txt"), b"t").unwrap();

        let mk = |name: &str| RenameItem {
            source_path: dir.join(name),
            file_name: name.to_string(),
            extension: ".txt".to_string(),
            is_dir: false,
        };
        let items = vec![mk("a.txt"), mk("b.txt")];
        // a.txt→b.txt（本批次占用，可交换）；b.txt→taken.txt（外部占用，应拒绝）；再加一个空闲名
        let names = vec!["b".to_string(), "taken".to_string()];
        let occ = build_occupancy(&items, &names, true);
        assert_eq!(occ.get("b.txt"), Some(&Occupant::InBatch));
        assert_eq!(occ.get("taken.txt"), Some(&Occupant::Outside));

        let names2 = vec!["fresh".to_string(), "also-fresh".to_string()];
        let occ2 = build_occupancy(&items, &names2, true);
        assert_eq!(occ2.get("fresh.txt"), Some(&Occupant::Free));

        // 大小写不敏感：目标 B.txt 与目录里的 b.txt 视为同名，且由本批次占用
        let names3 = vec!["B".to_string(), "x".to_string()];
        let occ3 = build_occupancy(&items, &names3, true);
        assert_eq!(occ3.get("B.txt"), Some(&Occupant::InBatch));

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn make_rows_reflects_plan_status() {
        let dir = std::env::temp_dir().join(format!("linerename-rows-{}", std::process::id()));
        let items = vec![RenameItem {
            source_path: dir.join("a.txt"),
            file_name: "a.txt".to_string(),
            extension: ".txt".to_string(),
            is_dir: false,
        }];
        let p = plan(&items, &["new".to_string()], true, &HashMap::new());
        let rows = make_rows(&items, Some(&p));
        assert_eq!(rows[0].target_name, "new.txt");
        assert_eq!(rows[0].status_kind, "ready");
        assert_eq!(rows[0].status_text, "就绪");
    }
}
