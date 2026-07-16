/**
 * 青苹果下载器：Google 表格端队列脚本
 *
 * 使用方法：在表格里框选包含 Drive 超链接的单元格，然后点击
 * 菜单中的“成品下载”。脚本只登记任务，不在云端搬运视频。
 * @OnlyCurrentDoc
 */

const GREEN_APPLE_QUEUE_SHEET = '_青苹果下载队列';
const GREEN_APPLE_HEADERS = [
  'task_id', 'batch_id', 'created_at', 'source_sheet', 'source_cell',
  'display_text', 'drive_url', 'file_id', 'status', 'progress',
  'local_path', 'error', 'requested_by', 'app_instance'
];

function onOpen() {
  SpreadsheetApp.getUi()
    .createMenu('🍏 成品下载')
    .addItem('成品下载', 'runGreenAppleDownload')
    .addToUi();
}

/**
 * 单按钮入口：发送本次框选链接，并安全地重试 FAILED 任务。
 * 全程只使用右下角 toast，不弹窗、不切换工作表。
 */
function runGreenAppleDownload() {
  const spreadsheet = SpreadsheetApp.getActive();
  const startedAt = Date.now();

  try {
    spreadsheet.toast('正在读取框选链接…', '🍏 成品下载', 3);
    const addedCount = enqueueSelectedDriveLinks_();

    // 只自动重试明确失败的任务。不要自动重置 DOWNLOADING，
    // 否则仍在下载的视频可能被重复加入队列。
    const retriedCount = resetFailedGreenAppleTasks_(false);
    SpreadsheetApp.flush();

    const seconds = ((Date.now() - startedAt) / 1000).toFixed(1);
    const retryText = retriedCount ? `，重试失败任务 ${retriedCount} 个` : '';
    const message = `已加入 ${addedCount} 个视频${retryText}（${seconds} 秒）`;
    console.log(message);
    spreadsheet.toast(message, '🍏 已完成', 6);
  } catch (error) {
    const message = error && error.message ? error.message : String(error);
    console.error(error && error.stack ? error.stack : message);
    spreadsheet.toast(`执行失败：${message}`, '🍏 成品下载', 8);
  }
}

/** 保留的独立入口；一般无需放进菜单。 */
function sendSelectedDriveLinks() {
  const spreadsheet = SpreadsheetApp.getActive();
  try {
    const addedCount = enqueueSelectedDriveLinks_();
    SpreadsheetApp.flush();
    spreadsheet.toast(`已加入 ${addedCount} 个视频。`, '🍏 已完成', 6);
  } catch (error) {
    const message = error && error.message ? error.message : String(error);
    console.error(error && error.stack ? error.stack : message);
    spreadsheet.toast(`执行失败：${message}`, '🍏 成品下载', 8);
  }
}

function enqueueSelectedDriveLinks_() {
  const spreadsheet = SpreadsheetApp.getActive();
  const rangeList = spreadsheet.getActiveRangeList();
  const ranges = rangeList ? rangeList.getRanges() : [spreadsheet.getActiveRange()];
  const validRanges = ranges.filter(Boolean);
  const totalCells = validRanges.reduce((sum, range) => sum + range.getNumRows() * range.getNumColumns(), 0);

  if (!totalCells) {
    throw new Error('请先框选包含 Google Drive 超链接的单元格');
  }
  if (totalCells > 500) {
    throw new Error('一次最多处理 500 个单元格，请缩小框选范围');
  }

  const batchId = Utilities.getUuid().replace(/-/g, '');
  const createdAt = new Date().toISOString();
  const requestedBy = Session.getActiveUser().getEmail() || '';
  const rows = [];
  const seenIds = new Set();

  validRanges.forEach(range => {
    if (range.getSheet().getName() === GREEN_APPLE_QUEUE_SHEET) return;
    const displayValues = range.getDisplayValues();
    const formulas = range.getFormulas();
    const richValues = range.getRichTextValues();

    for (let r = 0; r < range.getNumRows(); r++) {
      for (let c = 0; c < range.getNumColumns(); c++) {
        const link = findDriveLink_(richValues[r][c], formulas[r][c], displayValues[r][c]);
        const fileId = extractDriveFileId_(link);
        if (!fileId || seenIds.has(fileId)) continue;

        seenIds.add(fileId);
        rows.push([
          Utilities.getUuid().replace(/-/g, ''),
          batchId,
          createdAt,
          range.getSheet().getName(),
          range.getCell(r + 1, c + 1).getA1Notation(),
          displayValues[r][c] || fileId,
          link,
          fileId,
          'PENDING',
          0,
          '',
          '',
          requestedBy,
          ''
        ]);
      }
    }
  });

  if (!rows.length) {
    throw new Error('框选范围中没有识别到 Google Drive 文件链接');
  }

  const queue = ensureGreenAppleQueue_();
  queue.getRange(queue.getLastRow() + 1, 1, rows.length, GREEN_APPLE_HEADERS.length).setValues(rows);
  return rows.length;
}

/** 手动排错入口：会同时重置 FAILED 与 DOWNLOADING。 */
function retryFailedGreenAppleTasks() {
  const changed = resetFailedGreenAppleTasks_(true);
  SpreadsheetApp.flush();
  SpreadsheetApp.getActive().toast(`已重新发送 ${changed} 个失败或中断任务。`, '🍏 青苹果下载器', 5);
}

function resetFailedGreenAppleTasks_(includeDownloading) {
  const queue = ensureGreenAppleQueue_();
  const lastRow = queue.getLastRow();
  if (lastRow < 2) {
    return 0;
  }

  const values = queue.getRange(2, 1, lastRow - 1, GREEN_APPLE_HEADERS.length).getValues();
  let changed = 0;
  values.forEach(row => {
    const status = String(row[8]).toUpperCase();
    if (status === 'FAILED' || (includeDownloading && status === 'DOWNLOADING')) {
      row[8] = 'PENDING';
      row[9] = 0;
      row[10] = '';
      row[11] = '';
      row[13] = '';
      changed++;
    }
  });

  if (changed) {
    queue.getRange(2, 1, values.length, GREEN_APPLE_HEADERS.length).setValues(values);
  }
  return changed;
}

function showGreenAppleQueueSheet() {
  const queue = ensureGreenAppleQueue_();
  queue.showSheet();
  SpreadsheetApp.getActive().setActiveSheet(queue);
  SpreadsheetApp.getActive().toast('查看完后可以再次隐藏此工作表。', '下载队列', 5);
}

function ensureGreenAppleQueue_() {
  const spreadsheet = SpreadsheetApp.getActive();
  let sheet = spreadsheet.getSheetByName(GREEN_APPLE_QUEUE_SHEET);
  if (!sheet) {
    sheet = spreadsheet.insertSheet(GREEN_APPLE_QUEUE_SHEET);
    sheet.getRange(1, 1, 1, GREEN_APPLE_HEADERS.length).setValues([GREEN_APPLE_HEADERS]);
    sheet.setFrozenRows(1);
    sheet.getRange('A1:N1')
      .setBackground('#207A4A')
      .setFontColor('#FFFFFF')
      .setFontWeight('bold');
    sheet.hideSheet();
  } else {
    const headers = sheet.getRange(1, 1, 1, GREEN_APPLE_HEADERS.length).getValues()[0];
    if (headers.join('|') !== GREEN_APPLE_HEADERS.join('|')) {
      sheet.getRange(1, 1, 1, GREEN_APPLE_HEADERS.length).setValues([GREEN_APPLE_HEADERS]);
    }
  }
  return sheet;
}

function findDriveLink_(richText, formula, displayValue) {
  if (richText) {
    const directLink = richText.getLinkUrl();
    if (extractDriveFileId_(directLink)) return directLink;

    const runs = richText.getRuns ? richText.getRuns() : [];
    for (const run of runs) {
      const runLink = run.getLinkUrl();
      if (extractDriveFileId_(runLink)) return runLink;
    }
  }

  if (formula) {
    const hyperlinkMatch = formula.match(/^=HYPERLINK\(\s*"((?:[^"]|"")+)"/i);
    if (hyperlinkMatch) {
      const formulaLink = hyperlinkMatch[1].replace(/""/g, '"');
      if (extractDriveFileId_(formulaLink)) return formulaLink;
    }
    const formulaUrl = formula.match(/https?:\/\/drive\.google\.com\/[^"\s,)]+/i);
    if (formulaUrl && extractDriveFileId_(formulaUrl[0])) return formulaUrl[0];
  }

  if (displayValue) {
    const visibleUrl = String(displayValue).match(/https?:\/\/drive\.google\.com\/\S+/i);
    if (visibleUrl && extractDriveFileId_(visibleUrl[0])) return visibleUrl[0];
    if (extractDriveFileId_(displayValue)) return String(displayValue).trim();
  }

  return '';
}

function extractDriveFileId_(value) {
  if (!value) return '';
  const text = String(value).trim();
  const match = text.match(/(?:\/d\/|[?&]id=)([A-Za-z0-9_-]{15,})/i);
  if (match) return match[1];
  return /^[A-Za-z0-9_-]{20,}$/.test(text) ? text : '';
}
