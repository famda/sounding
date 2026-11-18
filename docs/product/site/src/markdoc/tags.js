import { Callout } from '@/components/Callout'
import { Download } from '@/components/Download'
import { ShellTabs } from '@/components/ShellTabs'
import { Tab, Tabs } from '@/components/Tabs'

const tags = {
  callout: {
    attributes: {
      title: { type: String },
      type: {
        type: String,
        default: 'note',
        matches: ['note', 'warning'],
        errorLevel: 'critical',
      },
    },
    render: Callout,
  },
  download: {
    selfClosing: true,
    attributes: {
      href: { type: String, required: true },
      title: { type: String, required: true },
      description: { type: String },
    },
    render: Download,
  },
  shell: {
    render: ShellTabs,
  },
  tabs: {
    render: Tabs,
  },
  tab: {
    render: Tab,
    attributes: {
      label: { type: String, required: true },
    },
  },
}

export default tags
