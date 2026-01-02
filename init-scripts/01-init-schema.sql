-- Create extension for UUID generation
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- Create receipts table
CREATE TABLE IF NOT EXISTS receipts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- File metadata
    file_name VARCHAR(255) NOT NULL,
    file_size_bytes BIGINT NOT NULL,
    mime_type VARCHAR(100) NOT NULL,
    photo_path TEXT NOT NULL,

    -- OCR results
    raw_ocr_text TEXT,
    extracted_fields JSONB,
    confidence_scores JSONB,

    -- Processing metadata
    processing_status VARCHAR(50) NOT NULL DEFAULT 'pending',
    processing_time_ms BIGINT,
    error_message TEXT,

    -- Timestamps
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    processed_at TIMESTAMP WITH TIME ZONE,

    -- Constraints
    CONSTRAINT chk_processing_status CHECK (
        processing_status IN ('pending', 'processing', 'completed', 'failed')
    )
);

-- Create indexes
CREATE INDEX IF NOT EXISTS idx_receipts_created_at ON receipts(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_receipts_status ON receipts(processing_status);
CREATE INDEX IF NOT EXISTS idx_receipts_filename ON receipts(file_name);
CREATE INDEX IF NOT EXISTS idx_receipts_extracted_fields_gin ON receipts USING GIN(extracted_fields);

-- Trigger to update updated_at timestamp
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ language 'plpgsql';

CREATE TRIGGER IF NOT EXISTS update_receipts_updated_at
    BEFORE UPDATE ON receipts
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();
