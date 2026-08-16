use argon2::{Argon2, PasswordHash, PasswordHasher, PasswordVerifier, password_hash::SaltString};
use rand_core::OsRng;
use unicode_normalization::UnicodeNormalization;

use crate::errors::{AppError, AppResult};

pub fn hash_password(password: &str) -> AppResult<String> {
    if password.chars().count() > 1024 {
        return Err(AppError::new(
            "USR-001",
            "パスワードは1024文字以内で入力してください。",
            "パスワードを短くして、もう一度お試しください。",
        ));
    }
    let salt = SaltString::generate(&mut OsRng);
    Argon2::default()
        .hash_password(password.as_bytes(), &salt)
        .map(|hash| hash.to_string())
        .map_err(|_| AppError::system("パスワードを安全に保存する準備ができませんでした。"))
}

pub fn verify_password(password: &str, encoded_hash: &str) -> bool {
    PasswordHash::new(encoded_hash).ok().is_some_and(|hash| {
        Argon2::default()
            .verify_password(password.as_bytes(), &hash)
            .is_ok()
    })
}

pub fn normalize_login_id(login_id: &str) -> String {
    login_id.trim().nfkc().collect::<String>().to_lowercase()
}

pub fn authentication_error() -> AppError {
    AppError::new(
        "AUTH-001",
        "ログインIDまたはパスワードが正しくありません。",
        "入力内容を確認してください。利用停止中の場合は管理者へ連絡してください。",
    )
}

pub fn login_required_error() -> AppError {
    AppError::new(
        "AUTH-002",
        "ログインが必要です。",
        "ログイン画面からログインしてください。",
    )
}

pub fn admin_required_error() -> AppError {
    AppError::new(
        "AUTH-003",
        "この操作には管理者権限が必要です。",
        "管理者ユーザーでログインしてください。",
    )
}
