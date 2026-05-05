# Asterisk.Sdk.Ami.SourceGenerators

Roslyn source generators for the Asterisk.Sdk.Ami package. Generates serialization and deserialization code at compile time, enabling Native AOT support with zero runtime reflection.

## Generators

- `ActionSerializerGenerator` — serializes AMI actions to protocol format
- `EventDeserializerGenerator` — deserializes AMI events from protocol messages
- `EventRegistryGenerator` — builds the event type registry
- `ResponseDeserializerGenerator` — deserializes AMI responses
