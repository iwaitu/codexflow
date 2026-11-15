const { slugify } = require('./slug');
test('lowercases and joins words', () => { expect(slugify('Hello World')).toBe('hello-world'); });
test('strips edge separators', () => { expect(slugify('  --Hi!-- ')).toBe('hi'); });
