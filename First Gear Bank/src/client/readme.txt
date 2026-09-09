This folder contains the player-facing banking interface: Banker dialogue, account balances, deposits, withdrawals,
transfers, CD offers and purchases, statements, and confirmation screens. It sends player requests to the server and
displays server-approved results, including errors and notifications. It never decides balances, interest rates, or
transaction outcomes. Banker spawning and lifecycle management remain in src/server, while financial rules remain in
src/core.