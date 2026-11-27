"""Marks, ratings and notes on catalog records and golden-seed elements, kept by category and id."""

from collections import defaultdict
from contextlib import closing, contextmanager
from datetime import datetime, timezone
import sqlite3

RATINGS = {"good", "maybe", "bad"}


def blank(category, id):
    return {"category": category, "id": id, "marked": False, "rating": None,
            "note": "", "revision": None, "updated": None}


class ReviewStore:
    def __init__(self, path):
        self.path = path
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.connection() as db:
            db.execute("""
                CREATE TABLE IF NOT EXISTS reviews (
                    category TEXT NOT NULL, id TEXT NOT NULL, marked INTEGER NOT NULL DEFAULT 0,
                    rating TEXT, note TEXT NOT NULL DEFAULT '', revision TEXT, updated TEXT,
                    PRIMARY KEY (category, id)
                )
                """)

    @contextmanager
    def connection(self):
        with closing(sqlite3.connect(self.path, timeout=10)) as db, db:
            db.row_factory = sqlite3.Row
            yield db

    def rows(self, category=None):
        with self.connection() as db:
            if category is None:
                rows = db.execute("SELECT * FROM reviews ORDER BY category, id")
            else:
                rows = db.execute("SELECT * FROM reviews WHERE category = ? ORDER BY id", (category,))
            return [dict(row) | {"marked": bool(row["marked"])} for row in rows]

    def patch(self, category, id, change, revision=None):
        if not isinstance(change, dict) or not change or set(change) - {"marked", "note", "rating"}:
            raise ValueError("Send marked, note, or rating to update a review.")
        if "marked" in change and type(change["marked"]) is not bool:
            raise ValueError("Marked must be true or false.")
        if "note" in change and not isinstance(change["note"], str):
            raise ValueError("A note must be text.")
        rating = change.get("rating")
        if rating is not None and (not isinstance(rating, str) or rating not in RATINGS):
            raise ValueError("Choose Good, Maybe, or Bad, or clear the rating.")
        changes = dict(change)
        if "rating" in changes:
            changes["revision"] = revision if rating else None
        changes["updated"] = datetime.now(timezone.utc).isoformat()
        with self.connection() as db:
            db.execute("INSERT OR IGNORE INTO reviews(category, id) VALUES (?, ?)", (category, id))
            fields = ", ".join(f"{field} = ?" for field in changes)
            db.execute(f"UPDATE reviews SET {fields} WHERE category = ? AND id = ?",
                       (*changes.values(), category, id))

    def clear_marks(self, category):
        with self.connection() as db:
            change = db.execute("UPDATE reviews SET marked = 0 WHERE category = ? AND marked = 1", (category,))
            return change.rowcount


class Reviews:
    """Reviews joined to the catalog: a rating given to an earlier revision of a record's files is shown as stale."""

    def __init__(self, store, revision):
        self.store = store
        self.revision = revision

    def item(self, category, id):
        return next((row for row in self.list(category) if row["id"] == id), blank(category, id))

    def list(self, category=None):
        return [self.project(row) for row in self.store.rows(category)]

    def project(self, row):
        row = dict(row)
        if row["rating"] and row["revision"] != self.revision(row["category"], row["id"]):
            row["previousRating"] = row["rating"]
            row["rating"] = None
        return row

    def patch(self, category, id, change):
        version = self.revision(category, id)
        if isinstance(change, dict) and "expectedRevision" in change:
            change = dict(change)
            expected = change.pop("expectedRevision")
            # A record can stay open while a tool replaces its files.
            if change.get("rating") and expected != version:
                raise ValueError("The record's files changed. Reload the page before rating it.")
        self.store.patch(category, id, change, version)
        return self.item(category, id)


def register_routes(router, workspace):
    """Register review and mark routes on the CMS router."""

    def list_reviews(req):
        rows = workspace.reviews().list(req.arg("category"))
        marked, rating, prefix = req.arg("marked"), req.arg("rating"), req.arg("prefix")
        return [
            row for row in rows
            if (marked != "1" or row["marked"])
            and (not rating or row["rating"] == rating)
            and (not prefix or row["id"].startswith(prefix))
        ]

    def get_review(req):
        return workspace.reviews().item(req.params["category"], req.params["id"])

    def patch_review(req):
        return workspace.reviews().patch(req.params["category"], req.params["id"], req.json())

    def clear_marks(req):
        return {"cleared": workspace.review_store.clear_marks(req.params["category"])}

    router.get("/api/reviews", list_reviews)
    router.get("/api/reviews/{category}/{id:path}", get_review)
    router.post("/api/reviews/{category}/{id:path}", patch_review)
    router.post("/api/marks/{category}/clear", clear_marks)
