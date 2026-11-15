# HostNames
Converts internationalized host names to their ASCII form (Python, tested with pytest).

## Expected behavior
`to_ascii(hostname)` lower-cases the name and returns its punycode form: `to_ascii("bücher.example")` is `xn--bcher-kva.example`.

## Dependencies
The conversion uses `idna`; the exact version is pinned in `requirements.txt`.

Test with `python -m pytest`.
