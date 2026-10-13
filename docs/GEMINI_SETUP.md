# Gemini 3.5 Flash-Lite

Потрібен `GEMINI_API_KEY` з Google AI Studio (підтримується також `GOOGLE_API_KEY`). Єдиний клієнт — `GeminiClient`; `AI_PROVIDER` і ключі попередніх провайдерів більше не використовуються. Модель зафіксована як `gemini-3.5-flash-lite`, інший model відхиляється.

Запит: `POST /v1beta/models/gemini-3.5-flash-lite:generateContent`, ключ у `x-goog-api-key`, `systemInstruction`, ролі `user/model`, `generationConfig.responseFormat.text` з JSON-схемою, `store=false`, thinking `MEDIUM` (пробне налаштування для діалогу). `store=false` керує журналюванням запиту, не є гарантією повного видалення чи відсутності обробки даних провайдером. Власні налаштування й правила акаунта Google мають значення.

`temperature`, `topP`, `topK`, `candidateCount`, frequency/presence penalties не надсилаються. За [офіційним посібником GenerateContent для 3.5 Flash-Lite](https://ai.google.dev/gemini-api/docs/generate-content/whats-new-gemini-3.6) sampling-параметри ігноруються; candidateCount більше не підтримується. [Google Cloud](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/gemini/3-5-flash-lite) також попереджає про помилку для frequency/presence penalties. У нашому клієнті цих штрафів немає.

За замовчуванням модель має MINIMAL, але раніше бот явно надсилав LOW. MEDIUM підтримується й тепер явно надсилається як експеримент; документація не гарантує кращої природності чату. Порівняй конкретні діалоги, затримку, reasoning tokens та завершеність JSON. Старі temperature/topP у зовнішньому generation.json приймаються для сумісності, ігноруються й викликають попередження в логах.

`AI_MAX_CONCURRENCY=2` задає одночасні генерації й HTTP-слоти. Після 429 спільна пауза враховує `Retry-After` або `RetryInfo`; до 3 HTTP-спроб на тому самому model для 429/5xx. Якщо HTTP-спроба з MEDIUM/HIGH вичерпала свій таймаут, клієнт один раз переходить на LOW і повторює той самий контекст, промпт та JSON-схему. Це входить у спільну межу 3 спроб і той самий бюджет задачі; наступні таймаути LOW не повторюються. Нове повідомлення, завершення бота й вичерпання загального бюджету не запускають резервну спробу. LOW підтримується моделлю й призначений для зменшення затримки, але успіх та якість цієї резервної відповіді не гарантовані. Денна квота повертає окрему помилку без тривалого повторення. Фактичні ліміти перевіряй у Google AI Studio: безкоштовна квота залежить від проєкту й може змінюватися. Нормальна відповідь не потребує окремих викликів для пам'яті чи книжок.

Повний бюджет задачі з побудовою контексту — 100 секунд, HTTP — до 45 секунд. Нове повідомлення скасовує старий запит, вихід/очищення/видалення також його скасовують. Уже прийняті запити можуть враховуватися провайдером навіть після скасування.

Логи запиту показують фактичні `HTTP timeout` і `job budget`, включно з параметрами із середовища. HTTP-таймаут журналюється окремо з тривалістю та рівнем thinking. `request_timeout` означає вичерпання часу HTTP-спроби, `job_budget_exhausted` — загального бюджету, `superseded true` — скасування через нову репліку або дію користувача. Не визначай причину затримки Google лише з факту таймауту. Якщо всі допустимі спроби завершились таймаутом, користувач отримує повідомлення про закінчення часу очікування.

Показується тільки завершена JSON-відповідь з `finishReason=STOP`. Thoughts і внутрішні метадані не відправляються в Telegram. Вміст, ключі, URL і тіла не логуються. Token usage зберігається в `ApiUsage` під model `google/gemini-3.5-flash-lite`; `metrics` показує `ai24h`.

```bash
bash scripts/bot.sh check-config
bash scripts/bot.sh metrics
```

Офіційні контракти: [GenerateContent](https://ai.google.dev/api/generate-content), [Thinking](https://ai.google.dev/gemini-api/docs/thinking), [Rate limits](https://ai.google.dev/gemini-api/docs/rate-limits), [API logging](https://ai.google.dev/gemini-api/docs/logging). Збірка не замінює ручну перевірку живого ключа й Telegram.
