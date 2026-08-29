# OrdoSort — domain glossary

Terms this project uses in a specific way, recorded when a decision actually
turned on one. Deliberately short: a term earns a place here by having been
resolved in a design conversation, not by seeming important. Add to it lazily,
the same way this file was started.

## Path identity

Whether two path strings name the same file.

Windows resolves paths case-insensitively and accepts several spellings that
don't name anything different: `.` and `..` segments, forward slashes, doubled
separators, a trailing separator on a folder. So a raw string comparison is not
an answer to this question, and for a while the codebase had three different
answers to it — one per caller who needed it.

**Path identity is decided in exactly one place: `PathIdentity` in
`OrdoSort.Core`.** Two paths are the same file when their canonical forms match
ordinal-case-insensitively. The canonical form is `Path.GetFullPath` with the
trailing separator trimmed. A path with no canonical form — invalid characters,
past the length limit, a malformed UNC — is never an exception; it is a value
callers can count.

Scope, decided rather than overlooked: **path identity here is a question about
spellings, not about the filesystem.** A mapped drive and its UNC form, a
symlink and its target, an 8.3 short name and its long one are *not* the same
file under this definition, because deciding that needs a file handle per path
and would put disk I/O inside a drag-drop handler. If that changes, it changes
behind `PathIdentity` and nothing above it moves.

Say **path identity** rather than "same path", "duplicate path", or "path
comparison" — the first two beg the question this term exists to answer, and the
third names a mechanism instead of the decision.

## Atomic placement

Getting a file to its destination without any reader ever seeing it
half-written: write to a sibling temp file, then move that into place in one
filesystem operation.

**Atomic placement lives in `AtomicPlace` in `OrdoSort.Core`.** Two rules a
caller no longer has to carry:

- The temp file is a **sibling** of the destination, never `%TEMP%` — the move
  is only atomic within one volume, and these files live on shares.
- The temp name carries a **GUID**, never a fixed `.tmp` — two stations saving
  the same file once shared one temp name, and one could install the other's
  bytes or find its own temp deleted mid-write.

Placement comes in two kinds, and the difference is about **ownership, not
mechanics**. Where a newer version is always correct — the config and its side
files — placement replaces what's there. Where the file belongs to whoever
created it — box labels — placement refuses, and a peer having won the race is
a *success*: their content is newer truth than the caller's snapshot. Replacing
in that second case reissued a box number already printed on a physical box.

### Atomic placement is not the created-by-me gate

These two read alike and are constantly confused, including in a review that
proposed merging them. They are not the same rule and they protect different
things.

Atomic placement owns a temp file **no other call can name**, so cleanup after
a failure is unconditional and the destination is never touched.

The **created-by-me gate** — in `Unlock.PlaceAndSwap`, `PdfMerge.MergeZipCore`
and `Zipper` — guards a *collision-freed* name, which a peer legitimately can
own. A free name proves only that it was free at check time, so those call
sites clean up **only what this call actually put on disk**. Deleting on that
assumption without the gate destroyed files other stations had written.

One is safe because the name is private. The other is careful because the name
is public. Don't merge them.
