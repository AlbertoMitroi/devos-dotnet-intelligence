# Open questions

Questions the engine team cannot answer alone. Independent work continues while they are open.

| Id | Question | Needed by | Owner | Default if unanswered |
|---|---|---|---|---|
| Q1 | GHCR creates a new package as private on first push. Making `ghcr.io/albertomitroi/dotnet-intelligence` public is a manual step in the GitHub UI. Who does it, and when? | Step 1h (first release) | repository owner | release blocked until the package is public |
| Q2 | Who reviews contract v1 from the consumer side before it is frozen as `1.0`? | Gate 2 | prompt author | publish as `1.0` after the Gate 2 review |
| Q3 | Are UTF-16 columns (D9) acceptable for the intended consumers? | Gate 2 | prompt author | keep UTF-16 |
| Q4 | Should symbols without a documentation comment id (D6) be modelled in v1? | Gate 2 | prompt author | out of scope in v1 |
