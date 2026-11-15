const { parseArgs } = require('./args');
test('reads a named option', () => { expect(parseArgs(['--name', 'Ada']).name).toBe('Ada'); });
test('defaults the name', () => { expect(parseArgs([]).name).toBe('world'); });
test('reads a flag and keeps the rest', () => { expect(parseArgs(['--verbose', 'a', 'b'])).toEqual({ name: 'world', verbose: true, rest: ['a', 'b'] }); });
