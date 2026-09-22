src/core contains the engine-independent banking rules and financial state: fixed-point money, interest-rate models,
financial-time calculations, savings and temporal-vault accrual, CD pricing and maturity, the double-entry journal,
account projections, transfers, recipient-name rules, request replay protection, notification records, statement data,
and audited registry/settlement recovery decisions.
It validates operations and builds consistent state changes. It has no knowledge of Vintage Story entities, inventories,
networking, or world-save APIs.
