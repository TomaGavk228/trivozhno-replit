# Google AI Studio — Gemini 3.5 Flash-Lite

Гілка `feat/gemini-3-5-flash-lite-2026-10-06` створена від `cd64edf984011c303e53abc5ed39c64dd7d4ce9f`. Активна модель: **`gemini-3.5-flash-lite`**.

## Запуск у Replit

1. Зупини Run.
2. Створи ключ у [Google AI Studio](https://aistudio.google.com/apikey) і додай його в Replit Secrets як `GEMINI_API_KEY`.
3. Зміни старий Secret `AI_PROVIDER=zai` на **`AI_PROVIDER=gemini`**. Значення Secret має перевагу над типовим значенням у коді.
4. У Shell з кореня проєкту:

```bash
git fetch origin
git switch --track origin/feat/gemini-3-5-flash-lite-2026-10-06
```

5. Натисни Run. Команда запуску: `bash scripts/run.sh`.

Для наступних оновлень:

```bash
git switch feat/gemini-3-5-flash-lite-2026-10-06
git pull --ff-only
```

Ключ не вставляй у Shell, Git чи чат. Застосунок читає Secrets як змінні середовища. `.env.example` — лише зразок; `.env` автоматично не завантажується. `DATABASE_URL`, `TELEGRAM_BOT_TOKEN` та інші Secrets меню зберігаються. `GOOGLE_API_KEY` також підтримується як альтернативне ім'я; цей клієнт надає перевагу `GEMINI_API_KEY`, якщо обидва задані.

Якщо задано `CHAT_CONTENT_PATH`, онови `generation.json` у тій папці або прибери перевизначення. Модель у логах має бути `gemini-3.5-flash-lite`. Невідповідність провайдера/моделі зупиняє запуск; помилка не запускає іншу модель.

## Запит за документацією Google

Нативний REST endpoint:

`POST https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash-lite:generateContent`

Авторизація: заголовок `x-goog-api-key`, ключ не потрапляє в URL. C# використовує наявний HttpClient; нові SDK чи пакети не потрібні.

- `systemInstruction.parts`: промпт, приклади та довідкова пам'ять.
- `contents`: реальні повідомлення; `assistant` перетворюється на роль Google `model`, повідомлення людини мають роль `user`. Сусідні повідомлення однієї ролі об'єднуються у впорядковані текстові частини. Останнє повідомлення залишається повним.
- `generationConfig.responseFormat.text`: `mimeType=APPLICATION_JSON`, `schema=ChatReplyFormat.Schema`. Використано актуальний формат нативної API reference; старі `responseSchema` / `_responseJsonSchema` в ній позначені deprecated. Це не `response_format` OpenAI і не лише прохання у промпті.
- `thinkingConfig`: `thinkingLevel=MINIMAL`, `includeThoughts=false`. Для цієї моделі minimal є підтримуваним рівнем, не гарантією нульових thinking-токенів.
- `candidateCount=1`, `maxOutputTokens=1600`, `temperature=1.0`, `topP=0.95`.
- `store=false`: вимикає логування запиту через цей параметр API; це не обіцянка умов обробки даних безкоштовного тарифу.

Параметри редагуються в `src/Bot/Resources/Chat/generation.json`: контекст 6000 оцінених токенів, до 16 попередніх обмінів, до 1000 токенів довідкової пам'яті. Оцінка UTF-8 консервативна й не дорівнює точному tokenizer Google. Схема та JSON-обгортки історії враховуються в локальному бюджеті. Промпт поведінки й приклади успадковані; ця зміна підключає провайдера, а не переписує персонажа.

## Відповідь і пам'ять

Один виклик повертає `reply`, `conversation_state`, `memory_updates`. У Telegram потрапляє тільки `reply`. Приймається лише кандидат з `finishReason=STOP`; `thought`-частини не показуються. JSON проходить строгий парсер наявного контракту. Пошкоджена чи обрізана відповідь дає помилку; окремої AI-генерації для ремонту, plain-text обходу або fallback-моделі немає.

Довготривалі факти, історія, старі підсумки, стан, книжки й дані меню залишаються в тих самих таблицях. Нової міграції не потрібно. Цитати для нових фактів проходять наявну перевірку за поточним повідомленням; очищення пам'яті й захист від запізнілих відповідей зберігаються.

## Ліміти та облік

`GEMINI_MAX_CONCURRENCY=1` — місцеве обмеження паралельності; старий `AI_MAX_CONCURRENCY` не збільшує паралельність Google-клієнта. Добові резервування AiQuota Groq не використовуються. Вигаданого ліміту Gemini у 190000 токенів/добу немає.

Після 429 застосовується спільна пауза за `Retry-After` або Google `RetryInfo.retryDelay`; за відсутності підказки — 2/4/6 секунд. До трьох HTTP-спроб у межах `AI_JOB_BUDGET_SECONDS`; 5xx також мають обмежені повтори. Відомий добовий ідентифікатор у QuotaFailure завершує запит як `daily_quota` без цих повторів; невідомий формат помилки класифікується як загальний 429. Місцевий лічильник або вигаданий час скидання не підміняє квоту Google.

Статистика записується в наявну ApiUsage з моделлю `google/gemini-3.5-flash-lite`. `Tokens` — totalTokenCount; `PromptTokens` — promptTokenCount, включно з кешованими; `CachedTokens` — cachedContentTokenCount; `ReasoningTokens` — thoughtsTokenCount; `CompletionTokens` — candidatesTokenCount + thoughtsTokenCount. Кеш/мислення не треба повторно додавати до total. Відсутній usage дає оцінку, позначену `usage reported False`. Це телеметрія, не допуск за квотою.

Google публікує безкоштовну вхідну й вихідну генерацію для цієї моделі. Реальні RPM/TPM/RPD визначає проєкт; перевір [ліміти AI Studio](https://aistudio.google.com/rate-limit). Новий ключ того самого проєкту не скидає квоту. В офіційному прайсі для Free Tier вказано використання даних для покращення продуктів; платний тариф має інші умови. Інтеграція не вмикає billing, grounding чи платні інструменти.

## Діагностика

Очікувані логи: `Chat configuration loaded ... model gemini-3.5-flash-lite`, `Gemini text request ... thinking MINIMAL ... format JSON schema`, `Gemini timing ... status 200`, `Gemini text response`.

| Помилка | Значення |
|---|---|
| `Set GEMINI_API_KEY in Secrets.` | Ключ не задано |
| `Invalid AI_PROVIDER/model combination` | Старий AI_PROVIDER або неправильна активна папка конфігурації |
| HTTP 400 / `INVALID_ARGUMENT` | Google відхилив параметри; звір активну конфігурацію |
| HTTP 400 / `API_KEY_INVALID`, `authentication` | Непридатний або прострочений ключ |
| HTTP 400 / `FAILED_PRECONDITION` | Проєкт/регіон/тариф не відповідає умовам Google; перевір AI Studio |
| HTTP 403 / `permission_denied` | Немає доступу або несумісні обмеження ключа |
| HTTP 404 / `model_unavailable` | Модель недоступна через цей endpoint/проєкт |
| HTTP 429 / `rate_limit` або `daily_quota` | Вичерпано квоту Google; перевір проєкт у AI Studio |
| `provider_blocked` | Google не повернув текст через свою перевірку |
| `incomplete_response` | Кандидат не завершений, зокрема через вихідний бюджет |
| `invalid_chat_envelope` | Повернений JSON не відповідає контракту бота |

У логах є HTTP-статус, числові показники та фіксовані категорії помилок; ключ, текст діалогу й повні помилки API не друкуються. Release-збірка перевіряє компіляцію. Живий запит і якість переписки перевіряються в Replit після додавання ключа; без нього інтеграція з акаунтом не підтверджена. Автоматичні тести не запускались за вказівкою користувача.

Офіційні джерела:

- [Gemini 3.5 Flash-Lite](https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash-lite)
- [REST generateContent, GenerationConfig, ResponseFormatConfig, ThinkingConfig](https://ai.google.dev/api/generate-content)
- [Structured output](https://ai.google.dev/gemini-api/docs/structured-output)
- [Thinking](https://ai.google.dev/gemini-api/docs/thinking)
- [API keys](https://ai.google.dev/gemini-api/docs/api-key)
- [Rate limits](https://ai.google.dev/gemini-api/docs/rate-limits)
- [Pricing](https://ai.google.dev/gemini-api/docs/pricing)
- [Troubleshooting](https://ai.google.dev/gemini-api/docs/troubleshooting)
