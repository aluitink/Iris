# S52 — Community post ignores post type: Article → Note, Poll → empty Note

- **Class:** bug — **Severity:** S2
- **Status:** open
- **Found:** Pass 323 (2026-09-22), broadened Pass 324
- **Related:** S10 (Article "(long-form)" mislabeled — fixed), S11 (Poll broken — fixed)

## Symptom

On the compose page with a community selected (`/compose?community=…`):

**Facet 1 — Article (Pass 323):**
1. Selecting "Article" from the post type dropdown and clicking "Post to community" → **HTTP 202**.
2. The Create activity's object has:
   - `id`: `…/u/ii-b1/notes/06GCNTCBBVZTFRBHM064MYE8X8` (a `notes/` IRI)
   - `type`: **"Note"** (not "Article")
3. The content is stored as plain text, not as an Article.

**Facet 2 — Poll (Pass 324):**
1. Selecting "Poll", filling in question + 2 options, clicking "Post to community" → **HTTP 202**.
2. The Create activity's object has:
   - `id`: `…/u/ii-b1/notes/06GCNWHTHSJH57QZSGAXNDG3Y0` (a `notes/` IRI)
   - `type`: **"Note"** (not "Question")
   - `content`: **empty** (no question text)
   - `oneOf`: **null** (no poll options)
3. The entire poll payload is **silently dropped** — the user gets a success message but an empty, contentless Note.

**Control test (no community, Pass 323):** Selecting "Article" on the regular compose page (`/compose`) → **HTTP 202** → object has:
- `id`: `…/u/ii-b1/articles/06GCNTJWHJ67CW0FZPPK7PF3QG` (an `articles/` IRI)
- `type`: **"Article"**

**The post type selection is silently ignored when posting to a community. For Polls, the data is additionally lost entirely.**

## Root cause hypothesis

The community post creation path does not pass the selected post type (Note/Article/Poll) to the server. It always creates a `Note` regardless of the dropdown selection. The regular compose path correctly forwards the type.

## Fix

The community post handler should respect the post type dropdown selection and create the appropriate object type (Note, Article, or Poll/Question) instead of always creating a Note. For Polls, the poll payload (question, options, duration, allowMultiple) must be passed through and serialized into the Question object.

## Re-verify

1. On `/compose?community=…`, select "Article", type content, click "Post to community".
2. Verify the Create activity's object has `type: "Article"` and an `articles/` IRI.
3. Repeat with "Poll" selected, fill in question + options, click "Post to community".
4. Verify the Create activity's object has `type: "Question"`, a non-empty `content` (question), and a non-null `oneOf` array with the options.
5. Verify the object renders correctly in the community feed (if S49 is fixed).
6. 0 console errors.
