SELECT '=== Objects rows: ObjectType + IsTombstoned for the 5 slugs ===';
SELECT "Id", "ObjectType", "IsTombstoned" FROM "Objects" WHERE "Id" LIKE '%qa-pass268b-test%' OR "Id" LIKE '%qa-pass268c-test%' OR "Id" LIKE '%qa-pass268d-test%' OR "Id" LIKE '%qa-pass268e-test%' OR "Id" LIKE '%qa-pass261-test%' ORDER BY "Id";
