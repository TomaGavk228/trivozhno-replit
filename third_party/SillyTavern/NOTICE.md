# Historical SillyTavern attribution

The SillyTavern-derived adapter and preset were removed on the
`feat/chat-reset-2026-10-05` branch. The project's AGPL-3.0 license is retained.
The notice below records attribution for the earlier implementation in Git
history; its file paths and behavior do not describe the current chat core.

# SillyTavern source adaptation

Upstream: https://github.com/SillyTavern/SillyTavern

Version: 1.19.0, release commit `06bde939fb1e9c4c8d8641d810f0a916b5bce127`.

Copyright: SillyTavern contributors. License: GNU Affero General Public License v3.0 (see the repository's `LICENSE`, copied verbatim from upstream).

Modified on 2026-10-04: ported the one-to-one text Chat Completion assembly and budgeting operations from JavaScript to C#, and localized an upstream Chat Completion preset for a Ukrainian Telegram character.

| Upstream source | Adaptation |
|---|---|
| `public/scripts/openai.js`: `MessageCollection`, `ChatCompletion` | `src/Bot/Infrastructure/SillyTavern/ChatCompletion.cs`: ordered collections, token reservation, affordability, insertion, chat serialization |
| `public/scripts/openai.js`: `preparePromptsForChatCompletion` | `TavernConfiguration.cs`: markers, personality/scenario templates, character main/PHI overrides and `{{original}}` |
| `public/scripts/openai.js`: `populateChatCompletion`, `populateChatHistory`, `populateDialogueExamples`, `prepareOpenAIMessages` | `TavernPromptBuilder.cs`: mandatory prompts first, new-chat reservation, newest history first, complete example blocks, configurable example/history allocation priority, preset output order |
| `src/prompt-converters.js`: `mergeMessages` example-name prefixing | Example messages use the original named-system-message representation, serialized as speaker-prefixed text before our GPT-OSS-compatible system-block merge |
| `default/content/presets/openai/Default.json` | `Default.upstream.json` is the unchanged original; `src/Bot/Resources/Conversation/sillytavern-ua.json` retains its preset structure and prompt order with localized prompts, Groq model and generation settings |

Exact upstream links:

- https://github.com/SillyTavern/SillyTavern/blob/06bde939fb1e9c4c8d8641d810f0a916b5bce127/public/scripts/openai.js
- https://github.com/SillyTavern/SillyTavern/blob/06bde939fb1e9c4c8d8641d810f0a916b5bce127/public/scripts/PromptManager.js
- https://github.com/SillyTavern/SillyTavern/blob/06bde939fb1e9c4c8d8641d810f0a916b5bce127/src/prompt-converters.js
- https://github.com/SillyTavern/SillyTavern/blob/06bde939fb1e9c4c8d8641d810f0a916b5bce127/default/content/presets/openai/Default.json

Scope: text-only, one character, one user, relative prompt slots, the seven listed character macros, ordinary replies. Browser UI, group chat, media, tool invocations, extensions runtime and the full macro interpreter are not included. Token counting uses the bot's conservative UTF-8 estimate, rather than the browser's `tokenHandler`. Example allocation has a 600-estimated-token ceiling. Existing session/memory/consent filters remain in the Telegram host. For GPT-OSS, post-history instructions use the documented user-role fallback so they retain their position after real history; this request-only application message is not stored as user conversation. The supplied compact example card is validated against its allowance instead of silently losing the final block. Our book evidence checks and observational continuity record are local integration code, not upstream SillyTavern features.

The AGPL-covered adapted source is included in this public repository. The bot's `/source` command links to this version and its license.
