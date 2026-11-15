# ArgsDemo
Parses command-line arguments (JavaScript, tested with Jest).

## Expected behavior
`parseArgs(argv)` returns `{ name, verbose, rest }`: `--name <value>` sets the name (default `world`), `--verbose` is a flag, everything else is kept in `rest`.

## Dependencies
Arguments are parsed with `minimist`; the versions are locked in `package-lock.json`.

Test with `npm test` (Jest).
