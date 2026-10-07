# Gemini 3.5 Flash-Lite

Потрібен `GEMINI_API_KEY` з Google AI Studio (підтримується також `GOOGLE_API_KEY`). Єдиний клієнт — `GeminiClient`; `AI_PROVIDER` і ключі попередніх провайдерів більше не використовуються. Модель зафіксована як `gemini-3.5-flash-lite`, інший model відхиляється.

Запит: `POST /v1beta/models/gemini-3.5-flash-lite:generateContent`, ключ у `x-goog-api-key`, `systemInstruction`, ролі `user/model`, `generationConfig.responseFormat.text` з JSON-схемою, `store=false`, thinking `LOW`. `store=false` керує журналюванням запиту, не є гарантією повного видалення чи відсутності обробки даних провайдером. Власні налаштування й правила акаунта Google мають значення.

`AI_MAX_CONCURRENCY=2` задає одночасні генерації й HTTP-слоти. Після 429 спільна пауза враховує `Retry-After` або `RetryInfo`; до 3 HTTP-спроб на тому самому model для 429/5xx. Денна квота повертає окрему помилку без тривалого повторення. Фактичні ліміти перевіряй у Google AI Studio: безкоштовна квота залежить від проєкту й може змінюватися. Нормальна відповідь не потребує окремих викликів для пам'яті чи книжок.

Повний бюджет задачі з побудовою контексту — 100 секунд, HTTP — до 45 секунд. Нове повідомлення скасовує старий запит, вихід/очищення/видалення також його скасовують. Уже прийняті запити можуть враховуватися провайдером навіть після скасування.

Показується тільки завершена JSON-відповідь з `finishReason=STOP`. Thoughts і внутрішні метадані не відправляються в Telegram. Вміст, ключі, URL і тіла не логуються. Token usage зберігається в `ApiUsage` під model `google/gemini-3.5-flash-lite`; `metrics` показує `ai24h`.

```bash
bash scripts/bot.sh check-config
bash scripts/bot.sh metrics
```

Офіційні контракти: [GenerateContent](https://ai.google.dev/api/generate-content), [Thinking](https://ai.google.dev/gemini-api/docs/thinking), [Rate limits](https://ai.google.dev/gemini-api/docs/rate-limits), [API logging](https://ai.google.dev/gemini-api/docs/logging). Збірка не замінює ручну перевірку живого ключа й Telegram.
