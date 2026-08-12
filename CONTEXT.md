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
