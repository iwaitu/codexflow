import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import openapiTS, { astToString } from 'openapi-typescript';

const __dirname = dirname(fileURLToPath(import.meta.url));
const desktopRoot = resolve(__dirname, '..');
const repoRoot = resolve(desktopRoot, '../..');
const contractsDir = resolve(desktopRoot, 'src/shared/contracts');

mkdirSync(contractsDir, { recursive: true });

// 1. OpenAPI -> TypeScript
const openApiPath = resolve(repoRoot, 'docs/contracts/openapi.json');
const openApiJson = JSON.parse(readFileSync(openApiPath, 'utf-8'));
const ast = await openapiTS(openApiJson, {
  alphabetize: true,
  exportType: true,
});
const openApiHeader = [
  '/* eslint-disable */',
  '// AUTO-GENERATED from docs/contracts/openapi.json by scripts/generate-contracts.mjs',
  '// Do not edit manually.',
  '',
].join('\n');
writeFileSync(resolve(contractsDir, 'api.generated.ts'), openApiHeader + astToString(ast), 'utf-8');

// 2. SSE JSON Schema -> TypeScript
const sseSchemaPath = resolve(repoRoot, 'docs/contracts/sse-events.schema.json');
const sseSchema = JSON.parse(readFileSync(sseSchemaPath, 'utf-8'));

function toPascalCase(name) {
  return name
    .split(/[^a-zA-Z0-9]+/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join('');
}

function schemaPropToTs(prop) {
  if (!prop) return 'unknown';
  if (prop.const !== undefined) {
    return JSON.stringify(prop.const);
  }
  if (Array.isArray(prop.enum)) {
    return prop.enum.map((v) => JSON.stringify(v)).join(' | ');
  }
  const t = prop.type;
  if (Array.isArray(t)) {
    return t.map((item) => schemaPropToTs({ ...prop, type: item })).join(' | ');
  }
  switch (t) {
    case 'string':
      return 'string';
    case 'integer':
    case 'number':
      return 'number';
    case 'boolean':
      return 'boolean';
    case 'null':
      return 'null';
    case 'array':
      return `Array<${schemaPropToTs(prop.items)}>`;
    case 'object':
      return 'Record<string, unknown>';
    default:
      return 'unknown';
  }
}

const defs = sseSchema.$defs ?? {};
const eventNames = sseSchema.properties?.event?.enum ?? Object.keys(defs);
const lines = [
  '/* eslint-disable */',
  '// AUTO-GENERATED from docs/contracts/sse-events.schema.json by scripts/generate-contracts.mjs',
  '// Do not edit manually.',
  '',
  `export const SSE_SCHEMA_VERSION = ${JSON.stringify(sseSchema.schemaVersion ?? '1.0.0')} as const;`,
  '',
  `export const SSE_KNOWN_EVENTS = [`,
  ...eventNames.map((name) => `  ${JSON.stringify(name)},`),
  `] as const;`,
  '',
  'export type KnownSseEventName = (typeof SSE_KNOWN_EVENTS)[number];',
  '',
];

for (const eventName of eventNames) {
  const def = defs[eventName];
  if (!def) continue;
  const typeName = `Sse${toPascalCase(eventName)}Payload`;
  const required = new Set(Array.isArray(def.required) ? def.required : []);
  const props = def.properties ?? {};
  lines.push(`export interface ${typeName} {`);
  for (const [propName, propSchema] of Object.entries(props)) {
    const opt = required.has(propName) ? '' : '?';
    const tsType = schemaPropToTs(propSchema);
    lines.push(`  ${propName}${opt}: ${tsType};`);
  }
  if (def.additionalProperties) {
    lines.push('  [key: string]: unknown;');
  }
  lines.push('}');
  lines.push('');
}

lines.push('export interface SseEventPayloadMap {');
for (const eventName of eventNames) {
  if (!defs[eventName]) continue;
  lines.push(`  ${JSON.stringify(eventName)}: Sse${toPascalCase(eventName)}Payload;`);
}
lines.push('}');
lines.push('');
lines.push('export type KnownSseEvent = {');
lines.push('  [K in keyof SseEventPayloadMap]: {');
lines.push('    id?: number;');
lines.push('    event: K;');
lines.push('    data: SseEventPayloadMap[K];');
lines.push('  };');
lines.push('}[keyof SseEventPayloadMap];');
lines.push('');
lines.push('export interface UnknownSseEvent {');
lines.push('  id?: number;');
lines.push('  event: string;');
lines.push('  data: Record<string, unknown>;');
lines.push('}');
lines.push('');
lines.push('export type ParsedSseEvent = KnownSseEvent | UnknownSseEvent;');
lines.push('');

writeFileSync(resolve(contractsDir, 'sse.generated.ts'), lines.join('\n'), 'utf-8');
console.log('Generated contracts:', resolve(contractsDir, 'api.generated.ts'), 'and sse.generated.ts');
