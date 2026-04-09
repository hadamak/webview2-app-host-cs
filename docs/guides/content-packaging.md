# Content Packaging

## Content source priority

`ZipContentProvider` resolves content in this order:

1. `www\` next to the EXE
2. ZIP path passed as the first command-line argument
3. ZIP with the same basename as the EXE
4. ZIP appended to the EXE
5. embedded `app.zip`

Resolution is file-by-file, not package-by-package.

## Packaging patterns

### Loose `www\`

Best for:

- local development
- rapidly changing content
- large files such as video, audio, or other assets better served directly from disk

### Launch-selected ZIP

Best for:

- shell-style hosts
- cartridge-style content switching
- automation that chooses content at startup

### Sibling ZIP

Best for:

- separate shipping of host and content
- replacing content without replacing the EXE

### Appended ZIP

Best for:

- single-file delivery
- content and executable distributed together as one artifact

### Embedded `app.zip`

Best for:

- a built-in default experience
- shipping a host that always has fallback content

## Build-time embedding

`src/WebView2AppHost.csproj` zips `web-content\` into `src\app.zip` before build and embeds it as a resource.

## Debug behavior

`Debug` builds additionally copy `test-www\` into the output directory as `www\`, which overrides the embedded resource because loose content has higher priority.
