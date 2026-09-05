//! Read-only interop boundary for UPDATED Tauri builds. This cannot change an
//! already-installed older executable; release/cutover must account for those.
use std::{
    fs::{self, Metadata},
    io,
    path::{Component, Path},
};

use crate::errors::{AppError, AppResult};

const PENDING_MARKERS: [&str; 2] = ["restore-pending.json", ".knowledgeapp-csharp-initializing"];

/// Only metadata for the fixed app root, its ancestors and two known markers is
/// inspected. Never parse a marker, inspect FAQ content, repair or delete data.
pub fn require_completed_recovery(root: &Path) -> AppResult<()> {
    check_with_metadata(root, |path| fs::symlink_metadata(path))
}

fn check_with_metadata(
    root: &Path,
    mut metadata: impl FnMut(&Path) -> io::Result<Metadata>,
) -> AppResult<()> {
    if !root.is_absolute()
        || root
            .components()
            .any(|part| matches!(part, Component::ParentDir | Component::CurDir))
    {
        return Err(recovery_required());
    }
    // Inspect from the volume down: never traverse an unchecked directory link
    // merely to determine whether a marker exists beneath it.
    for ancestor in root.ancestors().collect::<Vec<_>>().into_iter().rev() {
        match metadata(ancestor) {
            Ok(item) if item.is_dir() && !is_link(&item) => {}
            Ok(_) => return Err(recovery_required()),
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(()),
            Err(_) => return Err(recovery_required()),
        }
    }
    for name in PENDING_MARKERS {
        match metadata(&root.join(name)) {
            // A file, directory or dangling link all count as pending. Invalid
            // contents are not grounds to ignore a recovery/initialization flag.
            Ok(_) => return Err(recovery_required()),
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(_) => return Err(recovery_required()),
        }
    }
    Ok(())
}

fn is_link(metadata: &Metadata) -> bool {
    if metadata.file_type().is_symlink() {
        return true;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if metadata.file_attributes() & 0x400 != 0 {
            return true;
        }
    }
    false
}

fn recovery_required() -> AppError {
    AppError::new(
        "BK-012",
        "C#版の復元・初期化が未完了、または安全に確認できないため、旧版の起動を停止しました。",
        "C#版を起動して復旧完了を確認してください。記録やDBを削除しないでください。解決しない場合は管理者へ相談してください。",
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn assert_stopped(root: &Path) {
        let error = require_completed_recovery(root).unwrap_err();
        assert_eq!(error.code, "BK-012");
        assert!(error.action.contains("C#版を起動して復旧完了を確認"));
        assert!(error.action.contains("記録やDBを削除しない"));
        assert!(!error.message.contains(&root.display().to_string()));
    }

    #[test]
    fn missing_root_is_allowed_without_creating_anything() {
        let temporary = tempfile::tempdir().unwrap();
        let root = temporary.path().join("not-created").join("app");
        require_completed_recovery(&root).unwrap();
        assert!(temporary.path().read_dir().unwrap().next().is_none());
    }

    #[test]
    fn clean_root_and_completed_marker_are_read_only() {
        let temporary = tempfile::tempdir().unwrap();
        let root = temporary.path();
        fs::write(
            root.join(".knowledgeapp-csharp-initialized"),
            b"completed synthetic marker",
        )
        .unwrap();
        fs::write(root.join("synthetic-data-sentinel"), b"not a real database").unwrap();
        require_completed_recovery(root).unwrap();
        assert_eq!(root.read_dir().unwrap().count(), 2);
        assert_eq!(
            fs::read(root.join("synthetic-data-sentinel")).unwrap(),
            b"not a real database"
        );
    }

    #[test]
    fn every_pending_marker_blocks_without_parsing_or_modifying_contents() {
        for marker in PENDING_MARKERS {
            for contents in [b"".as_slice(), b"{}", b"invalid synthetic bytes\xff"] {
                let temporary = tempfile::tempdir().unwrap();
                let path = temporary.path().join(marker);
                fs::write(&path, contents).unwrap();
                assert_stopped(temporary.path());
                assert_eq!(fs::read(path).unwrap(), contents);
                assert_eq!(temporary.path().read_dir().unwrap().count(), 1);
            }
        }
    }

    #[test]
    fn directory_disguised_as_either_marker_blocks_without_deletion() {
        for marker in PENDING_MARKERS {
            let temporary = tempfile::tempdir().unwrap();
            let path = temporary.path().join(marker);
            fs::create_dir(&path).unwrap();
            fs::write(path.join("synthetic-sentinel"), b"retain").unwrap();
            assert_stopped(temporary.path());
            assert_eq!(
                fs::read(path.join("synthetic-sentinel")).unwrap(),
                b"retain"
            );
        }
    }

    #[test]
    fn non_directory_ancestor_is_refused_without_accessing_children() {
        let temporary = tempfile::tempdir().unwrap();
        let file = temporary.path().join("not-a-directory");
        fs::write(&file, b"retain").unwrap();
        assert_stopped(&file.join("app"));
        assert_eq!(fs::read(file).unwrap(), b"retain");
    }

    #[test]
    fn relative_or_parent_traversal_root_is_refused() {
        assert_stopped(Path::new("relative-root"));
        let temporary = tempfile::tempdir().unwrap();
        assert_stopped(&temporary.path().join("..").join("app"));
    }

    #[test]
    fn inaccessible_ancestors_or_markers_fail_closed_with_fixed_error() {
        let temporary = tempfile::tempdir().unwrap();
        let root = temporary.path();
        for denied in [
            root.to_path_buf(),
            root.join(PENDING_MARKERS[0]),
            root.join(PENDING_MARKERS[1]),
        ] {
            let error = check_with_metadata(root, |path| {
                if path == denied {
                    Err(io::Error::new(
                        io::ErrorKind::PermissionDenied,
                        "synthetic private error",
                    ))
                } else {
                    fs::symlink_metadata(path)
                }
            })
            .unwrap_err();
            assert_eq!(error.code, "BK-012");
            assert!(!error.message.contains("synthetic private error"));
            assert!(!error.action.contains("synthetic private error"));
        }
    }

    #[test]
    fn startup_checks_before_initialization_and_rechecks_after_database_open() {
        let source = include_str!("../lib.rs");
        let early = source
            .find("csharp_recovery_guard::require_completed_recovery(&app_data_dir)")
            .unwrap();
        let initialize = source
            .find("DataRootService::initialize(app_data_dir,")
            .unwrap();
        let cleanup = source
            .find("attachments::cleanup_stale_stages(&data_root)")
            .unwrap();
        let database = source
            .find("Database::open(&data_root.database_path())")
            .unwrap();
        let later = source
            .find("csharp_recovery_guard::require_completed_recovery(data_root.root())")
            .unwrap();
        let read_faq_data = source.find("database.list_categories()").unwrap();
        assert!(
            early < initialize
                && initialize < cleanup
                && cleanup < database
                && database < later
                && later < read_faq_data
        );
    }

    #[cfg(windows)]
    fn junction(link: &Path, target: &Path) {
        // Both paths are freshly created within this test's own TempDir; only
        // creation is delegated to cmd. Cleanup uses Rust filesystem operations.
        let output = std::process::Command::new("cmd")
            .args(["/D", "/C", "mklink", "/J"])
            .arg(link)
            .arg(target)
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "Could not create owned synthetic junction"
        );
    }

    #[cfg(windows)]
    #[test]
    fn junction_ancestor_is_rejected_before_any_child_probe() {
        let temporary = tempfile::tempdir().unwrap();
        let target = temporary.path().join("target");
        fs::create_dir(&target).unwrap();
        fs::write(target.join("synthetic-sentinel"), b"retain").unwrap();
        let link = temporary.path().join("junction");
        junction(&link, &target);
        let root = link.join("app");
        let mut visited: Vec<std::path::PathBuf> = Vec::new();
        let error = check_with_metadata(&root, |path| {
            visited.push(path.to_path_buf());
            fs::symlink_metadata(path)
        })
        .unwrap_err();
        assert_eq!(error.code, "BK-012");
        assert_eq!(visited.last(), Some(&link));
        assert_eq!(
            fs::read(target.join("synthetic-sentinel")).unwrap(),
            b"retain"
        );
        fs::remove_dir(link).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn junction_marker_is_not_followed_or_removed() {
        for marker in PENDING_MARKERS {
            let temporary = tempfile::tempdir().unwrap();
            let root = temporary.path().join("app");
            let target = temporary.path().join("unrelated-synthetic-target");
            fs::create_dir(&root).unwrap();
            fs::create_dir(&target).unwrap();
            fs::write(target.join("synthetic-sentinel"), b"retain").unwrap();
            let link = root.join(marker);
            junction(&link, &target);
            assert_stopped(&root);
            assert_eq!(
                fs::read(target.join("synthetic-sentinel")).unwrap(),
                b"retain"
            );
            fs::remove_dir(link).unwrap();
        }
    }
}
