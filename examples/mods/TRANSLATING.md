# Translating the game

A language mod changes the text the game draws: the shell's windows and labels, MechLab, the finance and mission
reports, briefings, debriefings, contracts, ComStar news, pilot dossiers, the credits, cockpit messages and mission
objectives, and the port's own screens (Options → Mods, Cockpit Controls, MercNet). Pictures that have English words
painted into them (the main menu buttons, some screen titles) stay English. A language mod changes text only: it does
not change the game, and without it the game shows its own text.

You need no tools besides a text editor that saves UTF-8 (Notepad, VS Code, Notepad++).

- [Make the template](#make-the-template)
- [Translate the strings](#translate-the-strings)
- [Translate briefings, contracts and news](#translate-briefings-contracts-and-news)
- [Letters](#letters)
- [Find what is left](#find-what-is-left)
- [Check the pages](#check-the-pages)
- [Share your translation](#share-your-translation)

## Make the template

Start the game, open **Options → Mods** and press **LANGUAGE TEMPLATE**. The game writes the folder
`Mods/my-language` with every text to translate, taken from your copy of the game:

| File | What is in it |
|---|---|
| `mod.json` | the mod's name and description: change `name`, `author` and `description` |
| `lang/interface.json` | the shell's and the port's own labels, windows and messages |
| `lang/items.json` | weapons, equipment, items, MechLab sections and MechLab messages |
| `lang/menu.json` | the in-mission menu |
| `lang/controls.json` | Cockpit Controls: group headings and the game's commands |
| `lang/mission.json` | mission objectives, unit, building and nav point names, inspection texts |
| `lang/dossiers.json` | pilot and AeroTech pilot names and their dossiers |
| `lang/rmg.json` | random missions and instant action: planets, factions, generated briefings and contracts |
| `lang/credits.json` | the credits |
| `lang/missing.json` | texts the game showed without a translation while you played (see below) |
| `lang/briefings/*.txt` | every briefing, debriefing and contract, in English, one file each |
| `lang/news/*.txt` | every ComStar news text, in English |
| `font-samples/*.json` | the letters each font of the game already has (see [Letters](#letters)) |
| `lang-report.txt` | the check of what you have translated so far (see [Check the pages](#check-the-pages)) |

Press **LANGUAGE TEMPLATE** again whenever you like: it keeps everything you have written, adds only texts that are
new, and never replaces a file you have changed. Keep working in `Mods/my-language`; the button always writes to
that folder.

To try your translation, press **RELOAD**, switch **My language** ON and press **ACCEPT**.

## Translate the strings

Each `lang/*.json` file is a list of `"English text": "translation"` pairs. The template leaves every translation
empty, and an empty one keeps the game's text, so you can translate a bit at a time:

```json
{
  "comment": "Write each translation between the empty quotes ...",
  "ACCEPT": "ÜBERNEHMEN",
  "Funds:": "Geld:",
  "Total confirmed kills: %d": "Bestätigte Abschüsse: %d",
  "Cash bounty for %d confirmed kills": ""
}
```

- Never change the English text on the left. It must stay exactly as the game shows it: `Exit` and `EXIT` are
  different texts, and so are `Funds:` and `Funds: ` with a space.
- `%d` stands for a number and `%s` for any text. The translation must keep the same `%d` / `%s` in the same order;
  if it does not, the game says so and leaves that line out.
- Inside the quotes, write `\"` for a quote, `\\` for a backslash and `\n` for a new line. A text such as
  `\\lExpenses:` has the game's own escapes in it (here `\l`); keep them.
- Some windows are put together from parts: `line|line#Button`. The game first looks for the whole text, then for
  each part, so you may translate either.
- Short labels sit in narrow columns. Keep a translation about as long as the English text.
- The `comment` line is only for you; the game skips it.
- The files may be split or renamed as you like: the game reads every `lang/*.json`.

## Translate briefings, contracts and news

`lang/briefings/NAME.txt` holds the whole text of one briefing, debriefing or contract, and `lang/news/NAME.txt` one
ComStar news text. Overwrite the English text with your translation and keep the file name. A file that is still the
game's English changes nothing.

The text uses the game's escapes. Keep them where they are:

| Escape | Meaning |
|---|---|
| `\n` | new line |
| `\t` | jump to the next tab stop (the `Planet:\t\tName` columns) |
| `\c` | this line is centred |
| `\l` | this line is drawn in the large font |
| `\s` | the next text starts on a new page |
| `\m` | the name of the player's unit |
| `\p` | the player's name |
| `\q` | text the game puts in (for example the kill table of a debriefing) |
| `\aNN`, `\bNNN`, `\gNNN` | links and positions used by the game; copy them unchanged |

Only `\n` starts a new line on the screen. Do not break a paragraph over several lines of the file: the game does not
treat a line break in the file as a space, so the last word of one line and the first of the next would run together.
Keep the line breaks that follow a `\n`, as in the English file.

A briefing shows two pages, a contract one, a dossier one. The [check](#check-the-pages) tells you when a translation
is longer than that.

Dossiers are not files: each dossier is one string in `lang/dossiers.json`, the English dossier on the left.

## Letters

The shell fonts of the game have the Latin letters, digits and signs, and these 22 accented letters:

```
Ç ü é â ä à ç ê ë è î Ä æ Æ ô ö û ù Ö Ü ú ß
```

Write your text in plain Unicode (UTF-8); the game finds these letters by itself. That is all of German and most of
French. The cockpit font has none of them. A letter a font does not have is drawn as its plain letter (`ñ` as `n`,
`ł` as `l`, `É` as `E`, and in the cockpit `ü` as `u`), or not at all when it has none (Cyrillic, Greek).

To add letters, draw them in `lang/fonts/NAME.json`, where NAME is the font:

| Font | Where |
|---|---|
| `shell11` | the text of briefings, contracts, news and dossiers, and many shell labels |
| `shell7` | the large lines (`\l`) of those texts, headings and many shell labels |
| `shell6` | the labels of the screens' text buttons; capital letters only; `shell9` and `shell10` use the same drawings |
| `shell5` | a small font the shell also loads; give it your letters too |
| `BASE6X76` | the cockpit: messages, objectives, menus |

```json
{
  "height": 10,
  "glyphs": {
    "ñ": [
      "..##.#",
      ".#.##.",
      "......",
      ".####.",
      ".#...#",
      ".#...#",
      ".#...#",
      ".#...#",
      ".#...#",
      "......"
    ]
  }
}
```

`#` is ink, `+` is the dark shadow some fonts have, `.` is empty. Every letter has exactly `height` rows, and all
rows of a letter are the same length (its width). `height` must be the font's own (a file with another height is
left out): `font-samples/NAME.json` holds
every letter the font already has, in this same format, so the easiest way is to copy a letter from there (for `ñ`,
copy `n`), give it its new name and add the accent. A letter you draw is used instead of the game's own accented one,
so you can also redraw those; the plain Latin letters and signs cannot be redrawn. A font with capital letters only (`shell6`, `shell9`, `shell10`) draws a small letter you did not draw as its capital,
so there you need only the capitals.

## Find what is left

Some texts are put together while the game runs, and the template cannot know them all. While a language mod is on,
the game writes every text it shows without a translation to `lang-missing.txt`, with numbers written as `%d`:

- Windows: `%USERPROFILE%\AppData\LocalLow\OpenWarThink\MW2Mercs\lang-missing.txt`
- macOS: `~/Library/Application Support/OpenWarThink/MW2Mercs/lang-missing.txt`

Play with your mod on, then press **LANGUAGE TEMPLATE**: those texts land in `lang/missing.json`. The list starts
empty each time the mods are applied.

## Check the pages

Each press of **LANGUAGE TEMPLATE** also checks what you have translated and writes `Mods/my-language/lang-report.txt`:

- a briefing or debriefing with more pages than the game's English text;
- a contract or dossier that no longer fits its page while the English one does;
- the letters your texts use that a font has no drawing of, and what is drawn instead.

The check puts `UNIT NAME` and `PILOT NAME` in for `\m` and `\p`, so a text that fits only with short names is
reported too. The message at the bottom of the Mods screen gives the number of problems.

## Share your translation

1. Rename the folder and the `id` in `mod.json` (for example `german`), and set `name`, `author`, `version` and
   `description`.
2. Leave out `font-samples`, `lang-report.txt`, and the files of `lang/briefings` and `lang/news` you have not
   translated (they are copies of the game's English and change nothing).
3. Zip the folder. A player unpacks it into `Mods`, switches it ON and presses ACCEPT.

Only one language at a time: if two language mods are on, the one lower in the list wins for each text.

MercNet does not compare language mods, so players with different languages can play together. A mod counts as a
language mod when all its files are in `lang`, `shell` (redrawn shell pictures) or `font-samples`; put anything else
(effects, rules) in a mod of its own, or every player will need your translation.
