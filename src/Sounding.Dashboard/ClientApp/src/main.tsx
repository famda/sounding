import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { applyStoredTheme } from './components/ThemeSwitch';
import './index.css';

applyStoredTheme();
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
