-- tables in service of RedShirt.Example.Api general template

CREATE TABLE IF NOT EXISTS DapperData
(
    id
    INT
    AUTO_INCREMENT,
    username
    VARCHAR
(
    50
) NOT NULL,
    email VARCHAR
(
    100
),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY
(
    id
)
    );