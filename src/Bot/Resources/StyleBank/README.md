# Style Bank

Production prompt uses `seed-chats.txt` as a small curated set of multi-turn examples. Keep this set small and high quality; do not paste entire external datasets into every Groq request.

## Recommended raw sources

### BYU Chit-Chat
- Repository: https://github.com/BYU-PCCL/chitchat-dataset
- Raw dataset: https://raw.githubusercontent.com/BYU-PCCL/chitchat-dataset/master/chitchat_dataset/dataset.json
- Human-human open-domain conversations; preserves multiple messages from the same speaker in a turn.
- MIT license.
- Best current source for messenger-like flow/rhythm.

### Amazon Topical-Chat
- Repository: https://github.com/alexa/Topical-Chat
- Human-human open-domain conversations with longer topic continuity.
- Data license: CDLA-Sharing 1.0; review attribution/redistribution obligations before copying data into a distributed product.
- Useful for longer multi-turn coherence, not as a direct Ukrainian voice source.

### Haptik conversation_data
- Repository: https://github.com/hellohaptik/conversation_data
- Contains context-response pairs where some assistant replies were written by humans and some by bots.
- ODbL / Database Contents License. Review obligations before reuse.
- Use only human-response samples and curate heavily.

## Curation rules

1. Prefer real multi-turn human-human or human-assistant fragments, not synthetic "ideal support" text.
2. Keep the whole local sequence (roughly 4-10 turns) so rhythm and conversational repair are visible.
3. Remove names, identifiers, URLs and private/sensitive facts.
4. Keep examples that show different actions: reaction, opinion, joke, disagreement, one question, advice when asked, topic shift, short answers and repair after a bad reply.
5. Translate/adapt to natural Ukrainian manually rather than machine-translating thousands of rows and feeding them blindly.
6. Never use external dataset text as facts about the current user.
7. Add only examples that make the bot sound better in A/B tests. More examples are not automatically better.

The long-term plan is to build a larger offline library from curated examples and, only if A/B tests show a benefit, retrieve a few relevant examples dynamically. The current runtime intentionally keeps the design simple and uses a small static seed bank.
