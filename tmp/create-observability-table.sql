-- Create observability_settings table in public schema
CREATE TABLE IF NOT EXISTS public.observability_settings (
    id SERIAL PRIMARY KEY,
    component VARCHAR(50) NOT NULL,
    retention_days INT NOT NULL DEFAULT 7,
    sampling_percent INT NOT NULL DEFAULT 100,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    updated_at TIMESTAMP NOT NULL,
    updated_by VARCHAR(255)
);

-- Unique constraint on component
CREATE UNIQUE INDEX IF NOT EXISTS ix_observability_settings_component 
    ON public.observability_settings (component);

-- Index for enabled components
CREATE INDEX IF NOT EXISTS ix_observability_settings_enabled 
    ON public.observability_settings (enabled);

-- Seed default settings
INSERT INTO public.observability_settings (component, retention_days, sampling_percent, enabled, updated_at, updated_by)
VALUES 
    ('prometheus', 7, 100, true, '2025-01-01 00:00:00'::timestamp, NULL),
    ('tempo', 7, 100, true, '2025-01-01 00:00:00'::timestamp, NULL),
    ('loki', 7, 100, true, '2025-01-01 00:00:00'::timestamp, NULL)
ON CONFLICT (component) DO NOTHING;
