import {
  createContext,
  type ReactNode,
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { knowledgeApi } from "../api/knowledgeApi";
import type { AppSettings, ColorTheme } from "../types/domain";
import { useAuth } from "./AuthContext";

interface ColorThemeContextValue {
  colorTheme: ColorTheme;
  showTopCategoryInTitle: boolean;
  showMascot: boolean;
  updateColorTheme: (nextTheme: ColorTheme) => Promise<void>;
  updateShowTopCategoryInTitle: (enabled: boolean) => Promise<void>;
  updateShowMascot: (enabled: boolean) => Promise<void>;
}

const ColorThemeContext = createContext<ColorThemeContextValue | null>(null);
const DEFAULT_SETTINGS: AppSettings = {
  colorTheme: "green",
  showTopCategoryInTitle: true,
  showMascot: true,
};

export function ColorThemeProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const [settings, setSettings] = useState<AppSettings>(DEFAULT_SETTINGS);
  const settingsRef = useRef(settings);
  const hasUserUpdated = useRef(false);

  useEffect(() => {
    hasUserUpdated.current = false;
    if (!user) {
      settingsRef.current = DEFAULT_SETTINGS;
      setSettings(DEFAULT_SETTINGS);
      return;
    }
    let active = true;
    knowledgeApi.getSettings()
      .then((loadedSettings) => {
        if (active && !hasUserUpdated.current) {
          settingsRef.current = loadedSettings;
          setSettings(loadedSettings);
        }
      })
      .catch(() => {
        if (active && !hasUserUpdated.current) {
          settingsRef.current = DEFAULT_SETTINGS;
          setSettings(DEFAULT_SETTINGS);
        }
      });
    return () => {
      active = false;
    };
  }, [user?.id]);

  useLayoutEffect(() => {
    document.documentElement.dataset.colorTheme = settings.colorTheme;
  }, [settings.colorTheme]);

  const updateSettings = useCallback(async (updates: Partial<AppSettings>) => {
    const previousSettings = settingsRef.current;
    const nextSettings = { ...previousSettings, ...updates };
    hasUserUpdated.current = true;
    settingsRef.current = nextSettings;
    setSettings(nextSettings);
    try {
      const savedSettings = await knowledgeApi.saveSettings(nextSettings);
      settingsRef.current = savedSettings;
      setSettings(savedSettings);
    } catch (error) {
      settingsRef.current = previousSettings;
      setSettings(previousSettings);
      throw error;
    }
  }, []);

  const updateColorTheme = useCallback(
    (nextTheme: ColorTheme) => updateSettings({ colorTheme: nextTheme }),
    [updateSettings],
  );

  const updateShowTopCategoryInTitle = useCallback(
    (enabled: boolean) => updateSettings({ showTopCategoryInTitle: enabled }),
    [updateSettings],
  );

  const updateShowMascot = useCallback(
    (enabled: boolean) => updateSettings({ showMascot: enabled }),
    [updateSettings],
  );

  const value = useMemo(
    () => ({
      colorTheme: settings.colorTheme,
      showTopCategoryInTitle: settings.showTopCategoryInTitle,
      showMascot: settings.showMascot,
      updateColorTheme,
      updateShowTopCategoryInTitle,
      updateShowMascot,
    }),
    [settings, updateColorTheme, updateShowMascot, updateShowTopCategoryInTitle],
  );

  return <ColorThemeContext.Provider value={value}>{children}</ColorThemeContext.Provider>;
}

export function useColorTheme() {
  const value = useContext(ColorThemeContext);
  if (!value) {
    throw new Error("useColorThemeはColorThemeProvider内で使用してください。");
  }
  return value;
}

export const useDisplaySettings = useColorTheme;
