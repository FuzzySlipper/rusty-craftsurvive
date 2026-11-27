"""Where the CMS reads from, and the browser mounts that serve those files."""

from dataclasses import dataclass
from pathlib import Path
from urllib.parse import quote

CMS = Path(__file__).resolve().parent
REPO = CMS.parents[2]
# Marks, ratings and notes: ignored with the other working assets, and backed up with them by assets-sync.sh.
DEFAULT_REVIEWS = REPO / "authoring/assets/cms/reviews.sqlite3"
DEFAULT_GOLDEN = REPO / "authoring/assets/golden-seed"
GOLDEN_CONFIG = REPO / "authoring/tools/golden-seed/golden-seed.json"

CONTENT_MOUNT = "/media/content/"
AUTHORING_MOUNT = "/media/authoring/"
GOLDEN_MOUNT = "/media/golden/"


@dataclass(frozen=True)
class Library:
    """The repository's content roots. Every path the CMS reads is under one of these."""

    repo: Path = REPO
    golden_dir: Path | None = None

    @property
    def content(self) -> Path:
        """What the product stages: the game's runtime files."""
        return self.repo / "content/game"

    @property
    def content_meta(self) -> Path:
        """The provenance files (`*.sources.json`) and Patch's own content beside content/game."""
        return self.repo / "content"

    @property
    def authoring_assets(self) -> Path:
        """Raw and working asset files (ignored, backed up to the NAS) and their tracked prompts and specs."""
        return self.repo / "authoring/assets"

    @property
    def golden(self) -> Path:
        return self.golden_dir or (self.repo / "authoring/assets/golden-seed")

    @property
    def mounts(self) -> dict[str, Path]:
        return {
            CONTENT_MOUNT: self.content,
            AUTHORING_MOUNT: self.authoring_assets,
            GOLDEN_MOUNT: self.golden,
        }

    def rel(self, path: Path) -> str:
        return path.resolve().relative_to(self.repo.resolve()).as_posix()

    def url(self, path: Path) -> str | None:
        """The browser URL of a file under one of the mounts, or None."""
        resolved = path.resolve()
        for mount, root in self.mounts.items():
            root = root.resolve()
            if resolved.is_relative_to(root):
                return mount + quote(resolved.relative_to(root).as_posix(), safe="/")
        return None

    def repo_path(self, relative: str) -> Path | None:
        """A repository-relative path, refused if it leaves the repository."""
        file = (self.repo / relative).resolve()
        return file if file.is_relative_to(self.repo.resolve()) else None
