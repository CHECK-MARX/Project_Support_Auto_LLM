use std::fmt::{self, Display, Formatter};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CliError {
    Input(String),
    Selector(String),
    Serialization(String),
}

impl CliError {
    pub const fn exit_code(&self) -> u8 {
        match self {
            Self::Input(_) => 2,
            Self::Selector(_) => 3,
            Self::Serialization(_) => 4,
        }
    }
}

impl Display for CliError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> fmt::Result {
        match self {
            Self::Input(message) | Self::Selector(message) | Self::Serialization(message) => {
                formatter.write_str(message)
            }
        }
    }
}

impl std::error::Error for CliError {}
