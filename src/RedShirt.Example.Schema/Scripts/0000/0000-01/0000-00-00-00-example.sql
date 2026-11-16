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

CREATE TABLE IF NOT EXISTS `UploadAggregate`
(
    `Id` CHAR(36) NOT NULL,
    `DateCreatedUtc` DATETIME(6) NOT NULL,
    `DateUpdatedUtc` DATETIME(6) NOT NULL,
    `UploadedByUserId` VARCHAR(256) NOT NULL,
    `State` INT NOT NULL,
    `FileName` VARCHAR(512) NOT NULL,
    `FileSizeBytes` BIGINT UNSIGNED NULL,
    -- ascii_bin: hex checksums require exact, case-sensitive equality for lookups and dedup
    `Sha256Checksum` CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
    `Flags` INT NOT NULL DEFAULT 0,
    `IdempotencyKey` VARCHAR(128) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE KEY `IX_UploadAggregate_IdempotencyKey` (`IdempotencyKey`),
    KEY `IX_UploadAggregate_Sha256Checksum` (`Sha256Checksum`)
);

CREATE TABLE IF NOT EXISTS `UploadEvent`
(
    `Id` CHAR(36) NOT NULL,
    `UploadId` CHAR(36) NOT NULL,
    `EventDateUtc` DATETIME(6) NOT NULL,
    `EventType` INT NOT NULL,
    `Json` JSON NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_UploadEvent_UploadId` (`UploadId`)
);

-- Background jobs run inside the API (see the API's BackgroundJobs projects). One row per job.
CREATE TABLE IF NOT EXISTS `BackgroundJob`
(
    `Id` CHAR(36) NOT NULL,
    `DateCreatedUtc` DATETIME(6) NOT NULL,
    `DateUpdatedUtc` DATETIME(6) NOT NULL,
    -- BackgroundJobType enum (1 = ValidateUpload, 2 = MoveUpload). Picks the handler.
    `Type` INT NOT NULL,
    -- BackgroundJobState enum: 0 Pending, 1 Running, 2 Succeeded, 3 Failed.
    `State` INT NOT NULL,
    -- The job type's parameters model.
    `ParametersJson` JSON NOT NULL,
    -- How many times the job has been leased.
    `Attempts` INT NOT NULL DEFAULT 0,
    -- Not leased before this time. Set by retry back-off.
    `DueAtUtc` DATETIME(6) NOT NULL,
    `LeaseOwner` VARCHAR(128) NULL,
    `LeaseExpiresAtUtc` DATETIME(6) NULL,
    `CompletedAtUtc` DATETIME(6) NULL,
    -- Detail reported with the most recent outcome, such as why an attempt failed.
    `Detail` TEXT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_BackgroundJob_State_DueAtUtc` (`State`, `DueAtUtc`),
    -- Used by the cleanup of old finished jobs.
    KEY `IX_BackgroundJob_State_CompletedAtUtc` (`State`, `CompletedAtUtc`),
    KEY `IX_BackgroundJob_Type` (`Type`)
);
