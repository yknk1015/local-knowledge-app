import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { knowledgeApi } from "../api/knowledgeApi";
import type { AuthenticatedUser } from "../types/domain";

interface AuthContextValue {
  user: AuthenticatedUser | null;
  loading: boolean;
  login: (loginId: string, password: string) => Promise<AuthenticatedUser>;
  logout: () => Promise<void>;
  refresh: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthenticatedUser | null>(null);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setUser(await knowledgeApi.getCurrentUser());
    } catch {
      setUser(null);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);
  useEffect(() => {
    const expire = () => setUser(null);
    window.addEventListener("knowledge-auth-expired", expire);
    return () => window.removeEventListener("knowledge-auth-expired", expire);
  }, []);

  const login = useCallback(async (loginId: string, password: string) => {
    const authenticated = await knowledgeApi.login(loginId, password);
    setUser(authenticated);
    return authenticated;
  }, []);

  const logout = useCallback(async () => {
    await knowledgeApi.logout();
    setUser(null);
  }, []);

  const value = useMemo(() => ({ user, loading, login, logout, refresh }), [user, loading, login, logout, refresh]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error("AuthProviderの内側でuseAuthを使用してください。");
  return value;
}
