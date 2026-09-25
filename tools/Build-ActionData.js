/**
 * Generates data/actions.json from a xivanalysis checkout.
 *
 * FFXIV_ACT_Plugin's resources tell us which actions are GCDs, but not how long their recast is.
 * xivanalysis maintains that by hand in src/data/ACTIONS/root/*.ts, and it is the only part of the
 * GCD calculation that cannot be observed from a log:
 *
 *   gcdRecast / cooldown  how long the action occupies the GCD. 2500 unless stated otherwise, but
 *                         dance steps run at 1000 and Ninjutsu / Hypercharge filler at 1500.
 *   speedAttribute        whether that recast scales with skill/spell speed at all. Dance steps
 *                         have none - they are a flat 1s no matter how much skill speed you have,
 *                         so their intervals must not be fed into the speed estimate.
 *   castTime              casts longer than the GCD push the next one out by caster tax.
 *
 * Plus the statuses that modify speed (haste), and the two jobs whose base GCD is inherently
 * faster than everyone else's.
 *
 * Usage:  node tools/Build-ActionData.js [path-to-xivanalysis] [out-file]
 */

'use strict';

const fs = require('fs');
const path = require('path');

const BASE_GCD = 2500;

const xivaRoot = process.argv[2] || path.join(__dirname, '..', '..', 'xivanalysis');
const outFile = process.argv[3] || path.join(__dirname, '..', 'data', 'actions.json');

const actionsDir = path.join(xivaRoot, 'src', 'data', 'ACTIONS', 'root');
const statusesDir = path.join(xivaRoot, 'src', 'data', 'STATUSES', 'root');

if (!fs.existsSync(actionsDir)) {
	console.error(`xivanalysis action data not found at ${actionsDir}`);
	console.error('Pass the path to a xivanalysis checkout as the first argument.');
	process.exit(1);
}

/**
 * Pulls out `KEY: { ... }` object literals by matching braces. The files are TypeScript with
 * imports and helper calls, so evaluating them would mean dragging in a compiler; every field we
 * want is a plain literal, and brace matching is enough to isolate each one.
 */
function* objectLiterals(source) {
	const header = /^\t([A-Z0-9_]+): \{$/gm;
	let match;

	while ((match = header.exec(source)) !== null) {
		let depth = 1;
		let i = match.index + match[0].length;

		while (i < source.length && depth > 0) {
			const ch = source[i];
			if (ch === '{') { depth++; }
			else if (ch === '}') { depth--; }
			i++;
		}

		yield { key: match[1], body: source.slice(match.index + match[0].length, i - 1) };
	}
}

const num = (body, field) => {
	// Only top-level fields: a nested object's "id:" would otherwise win.
	const m = new RegExp(`^\\t\\t${field}: (-?[0-9_]+(?:\\.[0-9]+)?),`, 'm').exec(body);
	return m ? Number(m[1].replace(/_/g, '')) : undefined;
};
const bool = (body, field) => new RegExp(`^\\t\\t${field}: true,`, 'm').test(body);
const speedAttr = (body) => {
	const m = /^\t\tspeedAttribute: Attribute\.(SKILL_SPEED|SPELL_SPEED),/m.exec(body);
	return m ? m[1] : undefined;
};

// ---------------------------------------------------------------- actions

const actions = [];
// Every action xivanalysis flags as rolling the GCD, whatever FFXIV's own category says. The
// category files a Ninja's mudras and Ninjutsu (and Monk's meditations, Samurai's Meditate) under
// "Ability", and the game data is right that they are not weaponskills - but they occupy the GCD,
// which is the only question uptime asks.
const onGcdIds = [];
let scanned = 0;

for (const file of fs.readdirSync(actionsDir).filter(f => f.endsWith('.ts'))) {
	const job = path.basename(file, '.ts');

	// Blue Mage and Limit Break never appear in the content this addon is aimed at, and BLU alone
	// would double the file for no benefit.
	if (job === 'BLU' || job === 'LIMIT_BREAK') { continue; }

	const source = fs.readFileSync(path.join(actionsDir, file), 'utf8');

	for (const { key, body } of objectLiterals(source)) {
		const id = num(body, 'id');
		if (id == null) { continue; }
		scanned++;

		if (!bool(body, 'onGcd')) { continue; }
		onGcdIds.push(id);

		const gcdRecast = num(body, 'gcdRecast');
		const cooldown = num(body, 'cooldown');
		const castTime = num(body, 'castTime');
		const attribute = speedAttr(body);

		let recast = gcdRecast != null ? gcdRecast
			: cooldown != null ? cooldown
				: BASE_GCD;

		// Falling back to `cooldown` picks up the ability cooldown for channelled actions that
		// happen to be flagged onGcd - Flamethrower comes out at 60000ms, which would hand its
		// caster a minute of "uptime" from one button. Nothing in the game occupies the GCD for
		// longer than Rainbow Drip's 6s, so anything past that is the wrong field, not a long GCD.
		if (recast > 10000) { recast = BASE_GCD; }

		// Only worth shipping when it says something the defaults do not.
		const interesting = recast !== BASE_GCD
			|| attribute == null
			|| (castTime != null && castTime >= BASE_GCD);
		if (!interesting) { continue; }

		const row = { id, name: key, job, recast };
		if (attribute != null) { row.speedAttribute = attribute; }
		if (castTime != null && castTime >= BASE_GCD) { row.castTime = castTime; }
		actions.push(row);
	}
}

// ---------------------------------------------------------------- statuses that alter speed

// xivanalysis records a status's speedModifier as one number and leaves "which actions" to the
// job module, so it has to be restated here. Inspiration (Starry Muse) reads "reduces cast time
// and recast time of aetherhue spells and Star Prism by 25%": the six aetherhue spells, their
// II versions, and Star Prism. Hammer Stamp/Brush/Polishing Hammer, Holy in White, Comet in
// Black, Rainbow Drip and every motif keep their normal recast - and the hammer combo is exactly
// what a Pictomancer presses inside Starry Muse. Applying the 0.75 to those expected a 1.875s
// GCD, saw 2.5s, and booked the difference as lost time on every single hammer.
const SPEED_STATUS_LIMITS = {
	3689: [ // INSPIRATION
		34650, 34651, 34652, // Fire in Red, Aero in Green, Water in Blue
		34653, 34654, 34655, // Blizzard in Cyan, Stone in Yellow, Thunder in Magenta
		34656, 34657, 34658, // Fire II in Red, Aero II in Green, Water II in Blue
		34659, 34660, 34661, // Blizzard II in Cyan, Stone II in Yellow, Thunder II in Magenta
		34681,               // Star Prism
	],
};

const speedStatuses = [];

for (const file of fs.readdirSync(statusesDir).filter(f => f.endsWith('.ts'))) {
	const source = fs.readFileSync(path.join(statusesDir, file), 'utf8');

	for (const { key, body } of objectLiterals(source)) {
		const id = num(body, 'id');
		const modifier = num(body, 'speedModifier');
		if (id == null || modifier == null) { continue; }

		const row = { id, name: key, job: path.basename(file, '.ts'), speedModifier: modifier };
		if (SPEED_STATUS_LIMITS[id]) { row.limitedTo = SPEED_STATUS_LIMITS[id]; }
		speedStatuses.push(row);
	}
}

// ---------------------------------------------------------------- output

const table = {
	_generator: 'tools/Build-ActionData.js - do not hand-edit, re-run the script instead',
	_source: 'xivanalysis src/data/ACTIONS/root and src/data/STATUSES/root',
	_generatedAt: new Date().toISOString().slice(0, 10),
	_recast: 'Milliseconds the action occupies the GCD. Actions matching the 2500ms default AND scaling with a speed stat are omitted - the consumer assumes those.',
	_speedAttribute: 'Absent means the recast does not scale with skill/spell speed at all (dance steps). Those intervals must not feed the speed estimate.',
	_limitedTo: 'On a speed status: the only action ids it hastens. Absent means every GCD the player has.',
	_onGcd: 'Every action id xivanalysis flags onGcd: true. Decides GCD-or-not ahead of the game category table, which files Ninja mudras/Ninjutsu, Monk meditations and Samurai Meditate as abilities although they roll the GCD.',

	baseGcd: BASE_GCD,

	// Monk and Ninja have an inherently faster base GCD than the flat 2.5s.
	// Mirrors JOB_SPEED_MODIFIERS in xivanalysis' speedStat adapter step.
	jobSpeedModifiers: { MNK: 0.8, NIN: 0.85 },

	actions: actions.sort((a, b) => a.id - b.id),
	speedStatuses: speedStatuses.sort((a, b) => a.id - b.id),
	onGcd: Array.from(new Set(onGcdIds)).sort((a, b) => a - b),
};

// The id list is a few hundred numbers; sixteen to a line keeps the file readable and the diff
// after a patch small.
const json = JSON.stringify(table, null, '\t').replace(/"onGcd": \[[^\]]*\]/, () => {
	const rows = [];
	for (let i = 0; i < table.onGcd.length; i += 16) {
		rows.push('\t\t' + table.onGcd.slice(i, i + 16).join(', '));
	}
	return '"onGcd": [\n' + rows.join(',\n') + '\n\t]';
});

fs.mkdirSync(path.dirname(outFile), { recursive: true });
fs.writeFileSync(outFile, json, 'utf8');

console.log(`==> scanned ${scanned} actions, wrote ${actions.length} GCD overrides, ${speedStatuses.length} speed statuses and ${table.onGcd.length} onGcd ids`);
console.log(`    ${outFile}`);

const byRecast = {};
for (const a of actions) { byRecast[a.recast] = (byRecast[a.recast] || 0) + 1; }
for (const [recast, count] of Object.entries(byRecast).sort((a, b) => a[0] - b[0])) {
	console.log(`      recast ${recast}ms: ${count}`);
}
console.log(`      no speed attribute (flat recast): ${actions.filter(a => !a.speedAttribute).length}`);
console.log(`      long casts (caster tax): ${actions.filter(a => a.castTime).length}`);
