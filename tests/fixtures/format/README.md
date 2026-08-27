# Format corpus

One package per schema version this project has ever written, plus a `.json`
beside each saying what it holds.

`FormatFixtureTests` opens every file here on every test run. That is the whole
point: §5.8 promises that a file OpenMCAD has written stays openable, and a
promise about old files can only be kept by keeping old files.

## Rules

- **Never regenerate a fixture.** The bytes are evidence about the build that
  wrote them. Rewriting them with a later build turns the corpus into a test
  that this build can read what this build just wrote, which every round-trip
  test already covers.
- **Never edit the `.json` to make a test pass.** If a fixture stops matching
  its description, either a migration is wrong or the description was. Work out
  which; do not settle it by editing the expectation.
- **Add one whenever `DocumentCodec.SchemaVersion` goes up**, in the same
  commit, along with the migration out of the version being left behind. A test
  fails until you do, and it writes a candidate package into the test output
  and tells you where.

## Naming

`schema-NNN.ompart` and `schema-NNN.json`, zero-padded to three digits so the
directory sorts the way the versions run.
