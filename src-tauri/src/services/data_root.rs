use std::{
    fs,
    path::{Path, PathBuf},
};

use uuid::Uuid;

use crate::errors::{AppError, AppResult};

const MANAGED_DIRECTORIES: &[&str] = &[
    "data",
    "attachments/articles",
    "manuals",
    "logs",
    "restore-staging",
    "safety-backups",
    "settings",
    "temp",
    "codex-bridge",
    "codex-bridge/delegations",
    "codex-inbox",
];

#[derive(Debug, Clone)]
pub struct DataRootService {
    root: PathBuf,
}

impl DataRootService {
    pub fn initialize(candidate: PathBuf, forbidden_roots: &[PathBuf]) -> AppResult<Self> {
        if !candidate.is_absolute() {
            return Err(Self::unsafe_path_error());
        }

        fs::create_dir_all(&candidate)
            .map_err(|_| AppError::system("利用者データの保存フォルダを作成できませんでした。"))?;

        let root = candidate
            .canonicalize()
            .map_err(|_| AppError::system("利用者データの保存先を確認できませんでした。"))?;

        for forbidden in forbidden_roots {
            if let Ok(forbidden) = forbidden.canonicalize() {
                if root == forbidden || root.starts_with(&forbidden) {
                    return Err(Self::unsafe_path_error());
                }
            }
        }

        for relative in MANAGED_DIRECTORIES {
            fs::create_dir_all(root.join(relative)).map_err(|_| {
                AppError::system("利用者データ用のフォルダを準備できませんでした。")
            })?;
        }

        Self::verify_read_write(&root)?;
        Ok(Self { root })
    }

    fn verify_read_write(root: &Path) -> AppResult<()> {
        let test_path = root
            .join("temp")
            .join(format!(".write-test-{}.tmp", Uuid::now_v7()));
        let test_bytes = b"knowledge-app-write-test";

        fs::write(&test_path, test_bytes)
            .map_err(|_| AppError::system("利用者データの保存先へ書き込めませんでした。"))?;
        let read_back = fs::read(&test_path)
            .map_err(|_| AppError::system("利用者データの保存先から読み取れませんでした。"))?;
        let _ = fs::remove_file(&test_path);

        if read_back != test_bytes {
            return Err(AppError::system(
                "利用者データの保存先で読み書きの確認に失敗しました。",
            ));
        }
        Ok(())
    }

    fn unsafe_path_error() -> AppError {
        AppError::system("利用者データの保存先が安全な場所ではないため、起動を中止しました。")
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn database_path(&self) -> PathBuf {
        self.root.join("data").join("knowledge.db")
    }

    pub fn attachments_path(&self) -> PathBuf {
        self.root.join("attachments").join("articles")
    }

    pub fn manuals_path(&self) -> PathBuf {
        self.root.join("manuals")
    }

    pub fn settings_path(&self) -> PathBuf {
        self.root.join("settings")
    }

    pub fn temp_path(&self) -> PathBuf {
        self.root.join("temp")
    }

    pub fn restore_staging_path(&self) -> PathBuf {
        self.root.join("restore-staging")
    }

    pub fn safety_backups_path(&self) -> PathBuf {
        self.root.join("safety-backups")
    }

    pub fn codex_bridge_path(&self) -> PathBuf {
        self.root.join("codex-bridge")
    }

    pub fn codex_delegations_path(&self) -> PathBuf {
        self.codex_bridge_path().join("delegations")
    }

    pub fn codex_inbox_path(&self) -> PathBuf {
        self.root.join("codex-inbox")
    }
}

pub fn find_git_root(start: &Path) -> Option<PathBuf> {
    start
        .ancestors()
        .find(|candidate| candidate.join(".git").exists())
        .map(Path::to_path_buf)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn creates_all_runtime_directories_outside_repository() {
        let temp = tempfile::tempdir().unwrap();
        let repository = temp.path().join("repository");
        let app_data = temp.path().join("local-app-data");
        fs::create_dir_all(&repository).unwrap();

        let service = DataRootService::initialize(app_data.clone(), &[repository]).unwrap();

        assert_eq!(
            service.database_path(),
            app_data
                .canonicalize()
                .unwrap()
                .join("data")
                .join("knowledge.db")
        );
        for relative in MANAGED_DIRECTORIES {
            assert!(app_data.join(relative).is_dir(), "missing {relative}");
        }
        assert!(!app_data.join("temp").read_dir().unwrap().any(|_| true));
    }

    #[test]
    fn refuses_a_data_root_inside_repository() {
        let temp = tempfile::tempdir().unwrap();
        let repository = temp.path().join("repository");
        let app_data = repository.join("data");
        fs::create_dir_all(&repository).unwrap();

        let error = DataRootService::initialize(app_data, &[repository]).unwrap_err();

        assert_eq!(error.code, "SYS-001");
        assert!(error.message.contains("安全な場所ではない"));
    }

    #[test]
    fn rejects_relative_paths_instead_of_falling_back() {
        let error = DataRootService::initialize(PathBuf::from("./data"), &[]).unwrap_err();
        assert!(error.message.contains("安全な場所ではない"));
    }
}
