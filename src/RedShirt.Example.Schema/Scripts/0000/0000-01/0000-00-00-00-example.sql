-- tables in service of RedShirt.Example.Api example implementations (ProductDto, OrderDto, CustomerDto)

CREATE TABLE IF NOT EXISTS `Product`
(
    `Id` CHAR(36) NOT NULL,
    `CreatedAtUtc` DATETIME(6) NOT NULL,
    `UpdatedAtUtc` DATETIME(6) NOT NULL,
    `Sku` VARCHAR(64) NOT NULL,
    `Name` VARCHAR(255) NOT NULL,
    `Price` DECIMAL(19, 4) NOT NULL,
    PRIMARY KEY (`Id`)
);

CREATE TABLE IF NOT EXISTS `Customers`
(
    `Id` CHAR(36) NOT NULL,
    `CreatedAtUtc` DATETIME(6) NOT NULL,
    `UpdatedAtUtc` DATETIME(6) NOT NULL,
    `Email` VARCHAR(320) NOT NULL,
    `DisplayName` VARCHAR(256) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_Customers_Email` (`Email`)
);

CREATE TABLE IF NOT EXISTS `Order`
(
    `Id` CHAR(36) NOT NULL,
    `CreatedAtUtc` DATETIME(6) NOT NULL,
    `UpdatedAtUtc` DATETIME(6) NOT NULL,
    `CustomerId` CHAR(36) NOT NULL,
    `Status` VARCHAR(64) NOT NULL,
    `TotalAmount` DECIMAL(19, 4) NOT NULL,
    `TotalPrice` DECIMAL(19, 4) NULL,
    PRIMARY KEY (`Id`)
);
