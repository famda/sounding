import type { SVGProps } from 'react';

type IconProps = SVGProps<SVGSVGElement> & { size?: number };

const base = (size = 16, className = ''): SVGProps<SVGSVGElement> => ({
  width: size,
  height: size,
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.75,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
  className,
});

export const IconCpu = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <rect x="4" y="4" width="16" height="16" rx="2" />
    <rect x="9" y="9" width="6" height="6" rx="1" />
    <path d="M9 1v3M15 1v3M9 20v3M15 20v3M20 9h3M20 14h3M1 9h3M1 14h3" />
  </svg>
);

export const IconMemory = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <path d="M6 19v-3M10 19v-3M14 19v-3M18 19v-3M8 11V9M16 11V9M12 11V9" />
    <path d="M2 15h20M2 7a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V7Z" />
  </svg>
);

export const IconGpu = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <rect x="2" y="6" width="20" height="12" rx="2" />
    <circle cx="8" cy="12" r="2.5" />
    <circle cx="16" cy="12" r="2.5" />
    <path d="M6 18v3M10 18v3M4 6V4" />
  </svg>
);

export const IconStorage = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <ellipse cx="12" cy="5" rx="9" ry="3" />
    <path d="M3 5v14c0 1.66 4 3 9 3s9-1.34 9-3V5" />
    <path d="M3 12c0 1.66 4 3 9 3s9-1.34 9-3" />
  </svg>
);

export const IconNetwork = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <path d="M7 16V4m0 0L3 8m4-4 4 4" />
    <path d="M17 8v12m0 0 4-4m-4 4-4-4" />
  </svg>
);

export const IconTopology = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <circle cx="5" cy="12" r="2.5" />
    <circle cx="19" cy="6" r="2.5" />
    <circle cx="19" cy="18" r="2.5" />
    <path d="M7.5 11 16.5 7M7.5 13l9 4" />
  </svg>
);

export const IconEvents = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <path d="M8 6h13M8 12h13M8 18h13" />
    <circle cx="3.5" cy="6" r="1" />
    <circle cx="3.5" cy="12" r="1" />
    <circle cx="3.5" cy="18" r="1" />
  </svg>
);

export const IconMaximize = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7" />
  </svg>
);

export const IconMinimize = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <path d="M4 14h6v6M20 10h-6V4M14 10l7-7M3 21l7-7" />
  </svg>
);

export const IconSearch = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <circle cx="11" cy="11" r="7" />
    <path d="m20 20-3.5-3.5" />
  </svg>
);

export const IconChevronRight = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <path d="m9 18 6-6-6-6" />
  </svg>
);

export const IconHelp = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <circle cx="12" cy="12" r="9" />
    <path d="M9.5 9.5a2.5 2.5 0 1 1 3.4 2.3c-.6.3-.9.8-.9 1.4v.3" />
    <path d="M12 17h.01" />
  </svg>
);

export const IconSun = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <circle cx="12" cy="12" r="4" />
    <path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41" />
  </svg>
);

export const IconMoon = ({ size = 16, className, ...rest }: IconProps) => (
  <svg {...base(size, className)} {...rest}>
    <path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5Z" />
  </svg>
);

export const Logo = ({ size = 30 }: { size?: number }) => (
  <img
    src={`${import.meta.env.BASE_URL}favicon.svg`}
    width={size}
    height={size}
    alt=""
    aria-hidden="true"
  />
);
