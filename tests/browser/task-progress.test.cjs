const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/WbsApp/wwwroot/js/task-progress.js'), 'utf8');

// Minimal input/select event surface; no browser or package dependency.
function form(wasCompleted, progressValue, statusValue) {
    const field = value => ({ value, listeners: {}, addEventListener(type, fn) { this.listeners[type] = fn; } });
    const progress = field(progressValue), status = field(statusValue), message = { textContent: '' };
    const element = { dataset: { wasCompleted: String(wasCompleted) },
        querySelector: selector => ({ '#Progress': progress, '#Status': status, '[data-progress-message]': message })[selector] };
    vm.runInNewContext(source, { document: { querySelector: () => element } });
    return { progress, status, message,
        changeProgress(value) { progress.value = value; progress.listeners.input(); },
        changeStatus(value) { status.value = value; status.listeners.change(); } };
}

test('entering 100 completes a task, choosing completed sets 100', () => {
    const ui = form(false, '25', '1'); ui.changeProgress('100');
    assert.equal(ui.status.value, '3');
    const other = form(false, '25', '0'); other.changeStatus('3');
    assert.equal(other.progress.value, '100');
});
test('last status selection is retained and requires explicit progress rather than 99', () => {
    const ui = form(false, '25', '1'); ui.changeStatus('3'); ui.changeStatus('2');
    assert.equal(ui.status.value, '2'); assert.equal(ui.progress.value, '');
    assert.match(ui.message.textContent, /0～99/);
    ui.changeProgress('30'); assert.equal(ui.status.value, '2');
});
test('reopening by status then progress requires both edits', () => {
    const ui = form(true, '100', '3'); ui.changeStatus('1');
    assert.equal(ui.progress.value, '100'); assert.match(ui.message.textContent, /再開/);
    ui.changeProgress('40'); assert.equal(ui.status.value, '1'); assert.equal(ui.message.textContent, '');
});
test('reopening by progress then status requires both edits', () => {
    const ui = form(true, '100', '3'); ui.changeProgress('40');
    assert.equal(ui.status.value, '3'); assert.match(ui.message.textContent, /100%に戻/);
    ui.changeStatus('0'); assert.equal(ui.progress.value, '40'); assert.equal(ui.message.textContent, '');
});
test('retrying rejected reopening preserves values and explains the correction', () => {
    const ui = form(true, '100', '2');
    assert.equal(ui.progress.value, '100'); assert.equal(ui.status.value, '2');
    assert.match(ui.message.textContent, /0～99/);
});
test('invalid or empty progress is retained for normal input validation', () => {
    const ui = form(false, '25', '1');
    for (const value of ['101', '-1', '99.5', '']) {
        ui.changeProgress(value); assert.equal(ui.status.value, '1'); assert.equal(ui.progress.value, value);
    }
});
