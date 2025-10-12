# Store Profiles Configuration (Phase 1)

Profiles are now loaded from XferLang `.xfer` files instead of being hardcoded.

1. Index file: `~/.poskernel/profiles.xfer`
2. Individual profile files: referenced by path entries inside the index file

Example `profiles.xfer`:

```
files {
  demo { path "~/.poskernel/profiles/demo-profile.xfer" }
}
```

Example `demo-profile.xfer`:

```
storeId "demo"
displayName "Demo Coffee Kiosk"
currency "USD"
culture "en-US"
version 1
paymentTypes {
  cash { allowsChange true requiresExact false }
  giftcard { allowsChange false requiresExact false }
  voucher_exact { allowsChange false requiresExact true }
}
```

Fail-fast rules:
- Missing index file: application exits with code 2
- Invalid / duplicate / missing required fields: application exits with descriptive message
- Empty profiles list: exit code 3

Temporary Note: `paymentTypes` are embedded only for early phases. They will later be sourced from a DB-backed NRF-compliant payment taxonomy. Do NOT extend logic here with business rules.
