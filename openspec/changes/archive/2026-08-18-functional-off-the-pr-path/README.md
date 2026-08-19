# functional-off-the-pr-path

Move the functional/Testcontainers matrix off every PR push and into the merge queue, unserialize it
from the unit suite, and cancel superseded PR runs — PR green ~29 → ~10 min, a landing ~60 → ~31 min
