# S52 — Community post ignores post type: Article selection creates a Note

- **Class:** bug — **Severity:** S2
- **Status:** open
- **Found:** Pass 323 (2026-09-22)
- **Related:** S10 (Article "(long-form)" mislabeled — fixed), S11 (Poll broken — fixed)

## Symptom

On the compose page with a community selected (`/compose?community=…`):

1. Selecting "Article" from the post type dropdown and clicking "Post to community" → **HTTP 202**.
2. The Create activity's object has:
   - `id`: `…/u/ii-b1/notes/06GCNTCBBVZTFRBHM064MYE8X8` (a `notes/` IRI)
   - `type`: **"Note"** (not "Article")
3. The content is stored as plain text, not as an Article.

**Control test (no community):** Selecting "Article" on the regular compose page (`/compose`) → **HTTP 202** → object has:
- `id`: `…/u/ii-b1/articles/06GCNTJWHJ67CW0FZPPK7PF3QG` (an `articles/` IRI)
- `type`: **"Article"**

**The post type selection is silently ignored when posting to a community.**

## Root cause hypothesis

The community post creation path does not pass the selected post type (Note/Article/Poll) to the server. It always creates a `Note` regardless of the dropdown selection. The regular compose path correctly forwards the type.

## Fix

The community post handler should respect the post type dropdown selection and create the appropriate object type (Note, Article, or Poll) instead of always creating a Note.

## Re-verify

1. On `/compose?community=…`, select "Article", type content, click "Post to community".
2. Verify the Create activity's object has `type: "Article"` and an `articles/` IRI.
3. Repeat with "Poll" selected — verify `type: "Question"` (or equivalent).
4. Verify the object renders correctly in the community feed (if S49 is fixed).
5. 0 console errors.
