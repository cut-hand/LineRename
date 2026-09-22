//! 两段式执行器：先把所有文件移入临时名，再统一落到目标名。
//! 任一步骤失败即按日志回滚；成功后写出撤销凭据。移动操作可注入以便测试。

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use crate::core::MoveOp;

/// 文件身份标识：卷序列号 + 文件索引，用于确认移动前后是同一个文件。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FileIdentity {
    pub volume: u32,
    pub index: u64,
}

#[cfg(windows)]
pub fn identity(path: &Path) -> io::Result<FileIdentity> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Storage::FileSystem::{
        CreateFileW, GetFileInformationByHandle, FILE_FLAG_BACKUP_SEMANTICS, FILE_SHARE_NONE,
        OPEN_EXISTING,
    };
    let wide: Vec<u16> = path.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
    unsafe {
        let handle = CreateFileW(
            wide.as_ptr(),
            0,
            FILE_SHARE_NONE,
            std::ptr::null(),
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            std::ptr::null_mut(),
        );
        if handle == windows_sys::Win32::Foundation::INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }
        let mut info: windows_sys::Win32::Storage::FileSystem::BY_HANDLE_FILE_INFORMATION = std::mem::zeroed();
        let ok = GetFileInformationByHandle(handle, &mut info);
        windows_sys::Win32::Foundation::CloseHandle(handle);
        if ok == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(FileIdentity {
            volume: info.dwVolumeSerialNumber,
            index: ((info.nFileIndexHigh as u64) << 32) | info.nFileIndexLow as u64,
        })
    }
}

#[cfg(not(windows))]
pub fn identity(_path: &Path) -> io::Result<FileIdentity> {
    Err(io::Error::new(io::ErrorKind::Unsupported, "identity only on windows"))
}

/// 同目录内的移动（rename）。可替换为注入的 fake 以测试回滚。
pub type Mover<'a> = &'a dyn Fn(&Path, &Path) -> io::Result<()>;

fn default_mover(from: &Path, to: &Path) -> io::Result<()> {
    fs::rename(from, to)
}

/// 执行结果：`moved` 每项为 原路径 → 最终路径（可直接作为撤销凭据）。
#[derive(Debug, Clone, PartialEq)]
pub struct ExecResult {
    pub moved: Vec<MoveOp>,
}

#[derive(Debug)]
pub struct ExecError {
    pub message: String,
}

impl std::fmt::Display for ExecError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.message)
    }
}
impl std::error::Error for ExecError {}

/// 生成同目录内的唯一临时名：.lr-{token}-{seq}.tmp
fn temp_name(token: &str, seq: usize) -> String {
    format!(".lr-{token}-{seq:04}.tmp")
}

fn token() -> String {
    use std::time::{SystemTime, UNIX_EPOCH};
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    format!("{:x}{:x}", nanos, std::process::id())
}

/// 执行两段式批量重命名。
///
/// 要求 `ops` 内所有文件位于同一目录（UI 按目录分批调用）。
/// 两阶段之间校验文件身份，防止阶段间被替换。
pub fn execute(ops: &[MoveOp], mover: Option<Mover<'_>>) -> Result<ExecResult, ExecError> {
    let mover = mover.unwrap_or(&default_mover);
    let token = token();
    let mut journal: Vec<MoveOp> = Vec::new();
    let mut staged: Vec<(PathBuf, MoveOp)> = Vec::with_capacity(ops.len());
    let mut identities: Vec<FileIdentity> = Vec::with_capacity(ops.len());

    // 记录源文件身份（必须在任何移动之前完成）
    for op in ops {
        match identity(&op.from) {
            Ok(id) => identities.push(id),
            Err(e) => {
                rollback(&journal, mover);
                return Err(ExecError {
                    message: format!("无法读取文件信息: {} ({e})", op.from.display()),
                });
            }
        }
    }

    // 阶段一：源 → 临时名
    for (i, op) in ops.iter().enumerate() {
        let dir = op.from.parent().unwrap_or_else(|| Path::new("")).to_path_buf();
        let tmp = dir.join(temp_name(&token, i));
        if let Err(e) = mover(&op.from, &tmp) {
            rollback(&journal, mover);
            return Err(ExecError {
                message: format!("第一阶段失败: {} → {} ({e})", op.from.display(), tmp.display()),
            });
        }
        journal.push(MoveOp { from: op.from.clone(), to: tmp.clone() });
        staged.push((tmp, op.clone()));
    }

    // 阶段间身份校验：每个临时文件必须是刚才那个源文件
    for (i, (tmp, _)) in staged.iter().enumerate() {
        match identity(tmp) {
            Ok(id) if id == identities[i] => {}
            Ok(_) => {
                rollback(&journal, mover);
                return Err(ExecError {
                    message: format!("文件在阶段间被替换: {}", tmp.display()),
                });
            }
            Err(e) => {
                rollback(&journal, mover);
                return Err(ExecError {
                    message: format!("无法校验临时文件: {} ({e})", tmp.display()),
                });
            }
        }
    }

    // 阶段二：临时名 → 目标名
    for (i, (tmp, op)) in staged.iter().enumerate() {
        if let Err(e) = mover(tmp, &op.to) {
            let mut all = journal.clone();
            all.extend(
                staged[..i]
                    .iter()
                    .map(|(t, o)| MoveOp { from: t.clone(), to: o.to.clone() }),
            );
            rollback(&all, mover);
            return Err(ExecError {
                message: format!(
                    "第二阶段失败: {} → {} ({e})，已回滚 {} 项",
                    tmp.display(),
                    op.to.display(),
                    i
                ),
            });
        }
    }

    Ok(ExecResult { moved: ops.to_vec() })
}

fn rollback(journal: &[MoveOp], mover: Mover<'_>) {
    for m in journal.iter().rev() {
        let _ = mover(&m.to, &m.from);
    }
}

/// 撤销凭据：每行 "源\t目标"
pub fn write_receipt(moved: &[MoveOp]) -> io::Result<PathBuf> {
    let dir = receipt_dir()?;
    fs::create_dir_all(&dir)?;
    let path = dir.join("last-undo.txt");
    let mut content = String::from("LINERENAME/1\n");
    for m in moved {
        content.push_str(&escape(&m.from));
        content.push('\t');
        content.push_str(&escape(&m.to));
        content.push('\n');
    }
    fs::write(&path, content)?;
    Ok(path)
}

/// 读取撤销凭据，返回逆操作（from=最终路径, to=原路径）。
pub fn read_receipt() -> io::Result<Vec<MoveOp>> {
    let path = receipt_dir()?.join("last-undo.txt");
    let text = fs::read_to_string(&path)?;
    let mut lines = text.lines();
    if lines.next() != Some("LINERENAME/1") {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "凭据格式不符"));
    }
    let mut ops = Vec::new();
    for line in lines {
        if line.is_empty() {
            continue;
        }
        let mut parts = line.split('\t');
        let (Some(from), Some(to)) = (parts.next(), parts.next()) else {
            continue;
        };
        ops.push(MoveOp {
            from: PathBuf::from(unescape(to)),
            to: PathBuf::from(unescape(from)),
        });
    }
    Ok(ops)
}

fn receipt_dir() -> io::Result<PathBuf> {
    if let Some(d) = std::env::var_os("LINERENAME_RECEIPT_DIR") {
        return Ok(PathBuf::from(d));
    }
    let base = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "LOCALAPPDATA 未设置"))?;
    Ok(base.join("LineRename"))
}

fn escape(p: &Path) -> String {
    let s = p.to_string_lossy();
    s.replace('%', "%25").replace('\t', "%09").replace('\n', "%0A").replace('\r', "%0D")
}

fn unescape(s: &str) -> String {
    let mut out = String::new();
    let mut chars = s.chars();
    while let Some(c) = chars.next() {
        if c == '%' {
            let h: String = chars.by_ref().take(2).collect();
            if let Ok(b) = u8::from_str_radix(&h, 16) {
                out.push(b as char);
                continue;
            }
            out.push('%');
            out.push_str(&h);
        } else {
            out.push(c);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;
    use std::sync::{Mutex, MutexGuard};

    static RECEIPT_LOCK: Mutex<()> = Mutex::new(());

    fn receipt_guard() -> MutexGuard<'static, ()> {
        RECEIPT_LOCK.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn tmpdir(tag: &str) -> PathBuf {
        let root = std::env::temp_dir().join(format!("linerename-exec-{tag}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(&root).unwrap();
        root
    }

    fn make_files(dir: &Path, names: &[&str]) -> Vec<PathBuf> {
        names
            .iter()
            .map(|n| {
                fs::write(dir.join(n), b"x").unwrap();
                dir.join(n)
            })
            .collect()
    }

    #[test]
    fn rollback_restores_all_files() {
        let dir = tmpdir("rollback");
        let files = make_files(&dir, &["a.txt", "b.txt", "c.txt"]);
        let calls = RefCell::new(0usize);
        let mover = |f: &Path, t: &Path| -> io::Result<()> {
            let mut n = calls.borrow_mut();
            *n += 1;
            if *n == 3 {
                return Err(io::Error::new(io::ErrorKind::PermissionDenied, "boom"));
            }
            fs::rename(f, t)
        };
        let ops: Vec<MoveOp> = files
            .iter()
            .enumerate()
            .map(|(i, f)| MoveOp { from: f.clone(), to: dir.join(format!("new{i}.txt")) })
            .collect();
        let err = execute(&ops, Some(&mover)).unwrap_err();
        assert!(err.message.contains("第一阶段"));
        for f in &files {
            assert!(f.exists(), "{} missing", f.display());
        }
        let leftover: Vec<_> = fs::read_dir(&dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| e.file_name().to_string_lossy().starts_with(".lr-"))
            .collect();
        assert!(leftover.is_empty(), "stale temp files: {leftover:?}");
    }

    #[test]
    fn successful_run_renames_all() {
        let dir = tmpdir("ok");
        let files = make_files(&dir, &["a.txt", "b.txt"]);
        let ops: Vec<MoveOp> = files
            .iter()
            .enumerate()
            .map(|(i, f)| MoveOp { from: f.clone(), to: dir.join(format!("n{i}.txt")) })
            .collect();
        let result = execute(&ops, None).unwrap();
        assert_eq!(result.moved.len(), 2);
        assert!(dir.join("n0.txt").exists());
        assert!(dir.join("n1.txt").exists());
        assert!(!dir.join("a.txt").exists());
    }

    #[test]
    fn swap_two_files_both_survive() {
        let dir = tmpdir("swap");
        fs::write(dir.join("a.txt"), b"a").unwrap();
        fs::write(dir.join("b.txt"), b"b").unwrap();
        let ops = vec![
            MoveOp { from: dir.join("a.txt"), to: dir.join("b.txt") },
            MoveOp { from: dir.join("b.txt"), to: dir.join("a.txt") },
        ];
        execute(&ops, None).unwrap();
        assert_eq!(fs::read(dir.join("a.txt")).unwrap(), b"b");
        assert_eq!(fs::read(dir.join("b.txt")).unwrap(), b"a");
    }

    #[test]
    fn receipt_round_trip() {
        let _g = receipt_guard();
        let dir = tmpdir("receipt");
        // 注入凭据目录，避免触碰真实 LOCALAPPDATA
        std::env::set_var("LINERENAME_RECEIPT_DIR", &dir);
        let ops = vec![MoveOp { from: dir.join("x.txt"), to: dir.join("y.txt") }];
        let path = write_receipt(&ops).unwrap();
        assert!(path.ends_with("last-undo.txt"));
        let back = read_receipt().unwrap();
        assert_eq!(back.len(), 1);
        assert_eq!(back[0].from, dir.join("y.txt"));
        assert_eq!(back[0].to, dir.join("x.txt"));
        std::env::remove_var("LINERENAME_RECEIPT_DIR");
    }

    #[test]
    fn receipt_escapes_special_chars() {
        assert_eq!(unescape(&escape(Path::new("a%b\tc"))), "a%b\tc");
    }

    #[test]
    fn receipt_ignores_malformed_lines() {
        let _g = receipt_guard();
        let dir = tmpdir("receipt2");
        std::env::set_var("LINERENAME_RECEIPT_DIR", &dir);
        let path = dir.join("last-undo.txt");
        fs::write(&path, "LINERENAME/1\na\tb\npartial\n\n").unwrap();
        let back = read_receipt().unwrap();
        assert_eq!(back.len(), 1);
        assert_eq!(back[0].from, PathBuf::from("b"));
        assert_eq!(back[0].to, PathBuf::from("a"));
        std::env::remove_var("LINERENAME_RECEIPT_DIR");
    }

    #[test]
    fn receipt_rejects_wrong_header() {
        let _g = receipt_guard();
        let dir = tmpdir("receipt3");
        std::env::set_var("LINERENAME_RECEIPT_DIR", &dir);
        fs::write(dir.join("last-undo.txt"), "GARBAGE\na\tb\n").unwrap();
        assert!(read_receipt().is_err());
        std::env::remove_var("LINERENAME_RECEIPT_DIR");
    }
}
