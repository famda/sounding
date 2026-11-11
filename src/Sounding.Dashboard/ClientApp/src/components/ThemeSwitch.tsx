import { useState } from 'react';
import { IconMoon, IconSun } from './Icons';

type Theme = 'light' | 'dark';
const KEY = 'sounding:theme';

function storedTheme(): Theme {
  try {
    return localStorage.getItem(KEY) === 'light' ? 'light' : 'dark';
  } catch {
    return 'dark';
  }
}

export function applyStoredTheme() {
  document.documentElement.dataset.theme = storedTheme();
}

export function ThemeSwitch() {
  const [theme, setTheme] = useState<Theme>(storedTheme);
  const choose = (next: Theme) => {
    setTheme(next);
    document.documentElement.dataset.theme = next;
    try { localStorage.setItem(KEY, next); } catch { /* storage unavailable */ }
  };

  return (
    <div className="flex items-center rounded border border-line bg-bg p-0.5" role="group" aria-label="Theme">
      {([['light', IconSun, 'Light'], ['dark', IconMoon, 'Dark']] as const).map(([value, Icon, label]) => (
        <button
          key={value}
          type="button"
          onClick={() => choose(value)}
          aria-pressed={theme === value}
          title={`${label} mode`}
          className={`rounded-xs p-1 transition ${theme === value ? 'bg-surface-3 text-text' : 'text-muted hover:text-text-2'}`}
        >
          <Icon size={13} />
        </button>
      ))}
    </div>
  );
}
