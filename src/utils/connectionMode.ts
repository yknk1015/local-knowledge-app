export function isSharedConnection() {
  return (window as Window & { __KNOWLEDGE_SHARED__?: boolean }).__KNOWLEDGE_SHARED__ === true;
}
