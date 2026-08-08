use serde::Serialize;

pub type AppResult<T> = Result<T, AppError>;

#[derive(Debug, Clone, Serialize, thiserror::Error)]
#[serde(rename_all = "camelCase")]
#[error("{code}: {message}")]
pub struct AppError {
    pub code: String,
    pub message: String,
    pub action: String,
}

impl AppError {
    pub fn new(code: &str, message: impl Into<String>, action: impl Into<String>) -> Self {
        Self {
            code: code.to_owned(),
            message: message.into(),
            action: action.into(),
        }
    }

    pub fn system(message: impl Into<String>) -> Self {
        Self::new(
            "SYS-001",
            message,
            "アプリを再起動してください。解決しない場合は診断情報を確認してください。",
        )
    }

    pub fn database(message: impl Into<String>) -> Self {
        Self::new(
            "DB-001",
            message,
            "アプリを終了し、データ保存先へアクセスできることを確認してから再起動してください。",
        )
    }
}

impl From<rusqlite::Error> for AppError {
    fn from(_: rusqlite::Error) -> Self {
        Self::database("データベースの処理に失敗しました。")
    }
}
