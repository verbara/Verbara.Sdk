# audiosocket-speaks-the-protocol-asterisk-speaks

Both AudioSocket implementations parse a frame header that Asterisk does not send, so neither has ever
completed a handshake. Fix the wire format against the real bytes, and pin it with them.
