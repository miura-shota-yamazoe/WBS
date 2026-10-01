"use strict";
const form = document.querySelector("[data-save-form]");
if (form) {
    const snapshot = () => new URLSearchParams(new FormData(form)).toString();
    const initial = snapshot();
    let submitting = false;
    const button = form.querySelector("button[type=submit]");
    const status = form.querySelector("[data-save-status]");
    window.addEventListener("beforeunload", event => {
        if (!submitting && snapshot() !== initial) {
            event.preventDefault();
            event.returnValue = "";
        }
    });
    form.addEventListener("submit", event => {
        if (submitting) { event.preventDefault(); return; }
        submitting = true;
        button.disabled = true;
        status.textContent = "保存中です…";
    });
    window.addEventListener("pageshow", () => {
        submitting = false;
        button.disabled = false;
        status.textContent = "";
    });
}
