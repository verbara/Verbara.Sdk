# Asterisk.Sdk

Core library for the Asterisk.Sdk ecosystem. Provides shared interfaces, attributes, enums, base types, and dependency injection abstractions used by all other packages.

## Features

- Common interfaces (`IAmiConnection`, `IAmiConnectionFactory`, `ManagerAction`, `ManagerEvent`, `ManagerResponse`)
- Attribute definitions for source-generated serialization
- Shared enums and base types for AMI, AGI, ARI protocols
- Native AOT compatible (no runtime reflection)
