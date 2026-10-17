"""Read-only integrity checks for a stopped, disposable Jellyfin fixture. No repair/migration."""

import pathlib
import shutil
import sqlite3
import sys
import tempfile


root = pathlib.Path(sys.argv[1])
databases = sorted(p for p in root.rglob("*") if p.is_file() and p.suffix in {".db", ".sqlite", ".sqlite3"})
if not databases:
    raise RuntimeError("No Jellyfin databases found; integrity verification did not run")

for path in databases:
    # SQLite may need to create/rebuild shared-memory WAL indexes even in mode=ro.
    # Copy the stopped database AND its journal family to disposable tmpfs, never use immutable=1
    # (which could silently ignore outstanding WAL transactions). Original files remain read-only.
    with tempfile.TemporaryDirectory() as temporary:
        snapshot = pathlib.Path(temporary) / path.name
        shutil.copyfile(path, snapshot)
        for suffix in ("-wal", "-shm", "-journal"):
            journal = pathlib.Path(str(path) + suffix)
            if journal.is_file():
                shutil.copyfile(journal, pathlib.Path(str(snapshot) + suffix))
        with sqlite3.connect(snapshot.as_uri() + "?mode=ro", uri=True) as connection:
            connection.execute("PRAGMA query_only=ON")
            result = connection.execute("PRAGMA integrity_check").fetchall()
            if result != [("ok",)]:
                raise RuntimeError(f"Database integrity check failed: {path.name}")
            if connection.execute("PRAGMA foreign_key_check").fetchone() is not None:
                raise RuntimeError(f"Database foreign-key check failed: {path.name}")
    print(f"PASS read-only SQLite integrity and foreign keys: {path.name}")
