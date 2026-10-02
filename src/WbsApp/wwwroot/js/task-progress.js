(() => {
    const form = document.querySelector("[data-was-completed]");
    if (!form) return;
    const progress = form.querySelector("#Progress");
    const status = form.querySelector("#Status");
    const message = form.querySelector("[data-progress-message]");
    const wasCompleted = form.dataset.wasCompleted === "true";
    const isHundred = () => Number(progress.value) === 100;
    const explain = () => {
        message.textContent = status.value === "3" && !isHundred()
            ? "ステータスが完了のまま保存すると、進捗率は100%に戻ります。再開する場合はステータスも変更してください。"
            : wasCompleted && status.value !== "3" && isHundred()
                ? "再開するには、進捗率も0～99%に変更してください。" : "";
    };
    progress.addEventListener("input", () => {
        // Reopening requires two explicit edits; never choose a non-completed status for the user.
        if (isHundred() && !wasCompleted) status.value = "3";
        explain();
    });
    status.addEventListener("change", () => {
        if (status.value === "3") progress.value = "100";
        else if (status.value !== "" && isHundred() && !wasCompleted) {
            // Keep the last selected status; ask for progress rather than inventing 99%.
            progress.value = "";
        }
        explain();
        if (status.value !== "3" && progress.value === "") message.textContent = "進捗率を0～99%で入力してください。";
    });
    explain();
})();
