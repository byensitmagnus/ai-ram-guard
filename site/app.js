const english = {
  title: 'Less RAM waste.\nMore room to work.', lead: 'See what Claude, Codex and Cursor use, and clean up the helpers they leave behind.', trust: 'Free · Open source · No account',
  windowsRequirements: 'Windows 10 / 11 · 64-bit', downloadWindows: 'Download for Windows', windowsStep1: 'Open the installer.', windowsStep2: 'Click Install. The app opens automatically.', windowsExtra: 'No unzipping or .NET installation. Starts with Windows.',
  macRequirements: 'macOS 13+ · Apple Silicon and Intel', downloadMac: 'Download for Mac', macStep1: 'Open the file and drag the app to Applications.', macStep2: 'Open AI RAM Guard from Applications.', macExtra: 'A native Mac app with memory monitoring and cleanup of leftover helpers.', macSigning: 'This beta is not notarized by Apple. macOS requires your approval the first time you open it.', macFirstOpen: 'How to approve the Mac beta',
  previewTitle: 'The overview, without the search.', previewCaption: 'Windows version', helpTitle: 'Good to know', firstOpenTitle: 'Windows or Mac asks you to approve the app',
  firstOpenWindows: 'Windows: If SmartScreen shows a warning, check that the file came from this GitHub project. Choose More info → Run anyway if you want to continue.',
  firstOpenMac: 'The Mac beta is not yet notarized by Apple. After trying to open it, go to System Settings → Privacy & Security → Open Anyway. Approval is only needed the first time.', appleHelp: 'Apple’s guide to opening an app',
  differenceTitle: 'Do the Windows and Mac versions have the same features?', difference: 'Both show AI app memory usage and can close leftover helpers they previously observed an AI app start. Windows also has config fixes with undo, memory trimming and cache moves. The Mac beta has memory and disk overviews. macOS does not support Windows working-set trimming.',
  updateTitle: 'I already have an older version', update: 'Install the new version over the old one. Your settings and history are kept. To remove it, use Windows Installed apps, or move the Mac app to the Trash.', report: 'Report a bug', allDownloads: 'All downloads and release notes'
};
const nodes = [...document.querySelectorAll('[data-copy]')];
const danish = Object.fromEntries(nodes.map(node => [node.dataset.copy, node.textContent]));
danish.title = 'Mindre RAM-spild.\nMere plads til dit arbejde.';
function setLanguage(language) {
  const copy = language === 'en' ? english : danish;
  document.documentElement.lang = language;
  nodes.forEach(node => { node.textContent = copy[node.dataset.copy]; });
  document.querySelectorAll('[data-language]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.language === language)));
  document.title = language === 'en' ? 'AI RAM Guard — download for Windows and Mac' : 'AI RAM Guard — download til Windows og Mac';
}
document.querySelectorAll('[data-language]').forEach(button => button.addEventListener('click', () => setLanguage(button.dataset.language)));
document.documentElement.dataset.platform = /mac/i.test(navigator.userAgentData?.platform || navigator.platform) ? 'mac' : 'windows';
if (new URLSearchParams(location.search).get('lang') === 'en') setLanguage('en');
document.querySelector('.first-open').addEventListener('click', () => { document.querySelector('#first-open').open = true; });
