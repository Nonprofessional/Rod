use std::fmt;

// The run's error vocabulary: one small enum instead of stringly errors, so
// the run loop matches on what happened rather than parsing prose.

/// Why a contact attempt ended. `Transport` walks the egress and retries;
/// `Protocol` is a wire break the run treats like a dropped cycle (defense
/// in depth -- sealed bodies should already have refused the bytes). A
/// permanent handshake refusal travels as `Attempt::Refused`, not an error:
/// it is an answer, not a failure.
#[derive(Debug)]
pub enum ContactError {
    Transport(String),
    Protocol(&'static str),
}

impl fmt::Display for ContactError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ContactError::Transport(cause) => write!(f, "transport: {cause}"),
            ContactError::Protocol(cause) => write!(f, "protocol: {cause}"),
        }
    }
}

impl From<ureq::Error> for ContactError {
    fn from(error: ureq::Error) -> Self {
        ContactError::Transport(error.to_string())
    }
}

impl From<std::io::Error> for ContactError {
    fn from(error: std::io::Error) -> Self {
        ContactError::Transport(error.to_string())
    }
}
