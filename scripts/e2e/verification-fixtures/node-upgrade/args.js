const minimist = require('minimist');

function parseArgs(argv) {
  const parsed = minimist(argv, { boolean: ['verbose'], string: ['name'], default: { verbose: false } });
  return { name: parsed.name || 'world', verbose: parsed.verbose, rest: parsed._ };
}
module.exports = { parseArgs };
