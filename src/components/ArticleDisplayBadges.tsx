export function localDateKey(date = new Date()) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

export function isBadgeActive(until: string | null, date = new Date()) {
  return Boolean(until && until >= localDateKey(date));
}

interface ArticleDisplayBadgesProps {
  newBadgeUntil: string | null;
  updatedBadgeUntil: string | null;
  isHidden?: boolean;
  showHidden?: boolean;
  showExpired?: boolean;
}

export function ArticleDisplayBadges({
  newBadgeUntil,
  updatedBadgeUntil,
  isHidden = false,
  showHidden = false,
  showExpired = false,
}: ArticleDisplayBadgesProps) {
  const newActive = isBadgeActive(newBadgeUntil);
  const updatedActive = isBadgeActive(updatedBadgeUntil);

  return (
    <>
      {(newActive || (showExpired && newBadgeUntil)) && (
        <span
          className={`display-badge new${newActive ? "" : " expired"}`}
          title={`表示終了日：${newBadgeUntil}`}
        >
          {newActive ? "新着" : "新着期限切れ"}
        </span>
      )}
      {(updatedActive || (showExpired && updatedBadgeUntil)) && (
        <span
          className={`display-badge updated${updatedActive ? "" : " expired"}`}
          title={`表示終了日：${updatedBadgeUntil}`}
        >
          {updatedActive ? "更新" : "更新期限切れ"}
        </span>
      )}
      {showHidden && isHidden && <span className="display-badge hidden">非表示</span>}
    </>
  );
}
