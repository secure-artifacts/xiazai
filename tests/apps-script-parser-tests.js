const fs = require('fs');
const vm = require('vm');
const path = require('path');

const code = fs.readFileSync(path.join(__dirname, '..', 'apps-script', 'Code.gs'), 'utf8');
const context = vm.createContext({ console });
vm.runInContext(code + '\nthis.extractDriveFileIdForTest = extractDriveFileId_; this.findDriveLinkForTest = findDriveLink_;', context);

const cases = [
  ['https://drive.google.com/file/d/1RNMSPtmZMNFMsV5H-1B_m3sk82tReSxV/view', '1RNMSPtmZMNFMsV5H-1B_m3sk82tReSxV'],
  ['https://drive.google.com/open?id=1RNMSPtmZMNFMsV5H-1B_m3sk82tReSxV&usp=drive_copy', '1RNMSPtmZMNFMsV5H-1B_m3sk82tReSxV'],
  ['not a drive link', '']
];

for (const [input, expected] of cases) {
  const actual = context.extractDriveFileIdForTest(input);
  if (actual !== expected) throw new Error(`Expected ${expected}, received ${actual} for ${input}`);
}

console.log('All Apps Script parser tests passed.');

const richTextLink = {
  getLinkUrl: () => 'https://drive.google.com/file/d/1RICHBBBBBBBBBBBBBBBBBBBBBBBBBBB/view',
  getRuns: () => []
};
if (context.findDriveLinkForTest(richTextLink, '', '显示文字') !== richTextLink.getLinkUrl()) {
  throw new Error('Rich-text hyperlink was not recognized');
}

const mixedRichText = {
  getLinkUrl: () => null,
  getRuns: () => [
    { getLinkUrl: () => null },
    { getLinkUrl: () => 'https://drive.google.com/open?id=1RUNCCCCCCCCCCCCCCCCCCCCCCCCCCCC' }
  ]
};
if (!context.findDriveLinkForTest(mixedRichText, '', '多段文字').includes('1RUN')) {
  throw new Error('Hyperlink in a rich-text run was not recognized');
}

if (!context.findDriveLinkForTest(null, '=HYPERLINK("https://drive.google.com/file/d/1FORMULADDDDDDDDDDDDDDDDDDDDDDD/view","成品")', '成品').includes('1FORMULA')) {
  throw new Error('HYPERLINK formula was not recognized');
}

console.log('All Apps Script rich-link tests passed.');
