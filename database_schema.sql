-- WARNING: This schema is for context only and is not meant to be run as-is.
-- It reflects the current Fix My Device backend tables plus the Emergency Recovery
-- phase-one additions for approved recovery scope and metadata-only file listings.

CREATE TABLE public.app_users (
  id uuid NOT NULL DEFAULT gen_random_uuid(),
  email text NOT NULL UNIQUE,
  password_hash text NOT NULL,
  token text NOT NULL UNIQUE,
  agent_setup_code text NOT NULL UNIQUE,
  created_at timestamp with time zone DEFAULT now(),
  CONSTRAINT app_users_pkey PRIMARY KEY (id)
);

CREATE TABLE public.devices (
  id uuid NOT NULL DEFAULT gen_random_uuid(),
  user_id uuid,
  device_name text,
  processor text,
  processor_speed text,
  installed_ram text,
  usable_ram text,
  graphics_card text,
  graphics_memory text,
  total_storage text,
  used_storage text,
  free_storage text,
  device_id text,
  product_id text,
  system_type text,
  windows_edition text,
  windows_version text,
  os_build text,
  installed_on text,
  status text,
  last_seen_at timestamp with time zone,
  drives_json jsonb,
  created_at timestamp with time zone DEFAULT now(),
  updated_at timestamp with time zone DEFAULT now(),
  CONSTRAINT devices_pkey PRIMARY KEY (id),
  CONSTRAINT devices_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.app_users(id)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_devices_user_hardware
  ON public.devices(user_id, device_id);

CREATE TABLE public.recovery_settings (
  id uuid NOT NULL DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL,
  device_id uuid NOT NULL,
  device_name text NOT NULL,
  enabled boolean NOT NULL DEFAULT false,
  approved_locations jsonb NOT NULL DEFAULT '[]'::jsonb,
  last_synced_at timestamp with time zone,
  created_at timestamp with time zone DEFAULT now(),
  updated_at timestamp with time zone DEFAULT now(),
  CONSTRAINT recovery_settings_pkey PRIMARY KEY (id),
  CONSTRAINT recovery_settings_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.app_users(id),
  CONSTRAINT recovery_settings_device_id_fkey FOREIGN KEY (device_id) REFERENCES public.devices(id)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_recovery_settings_owner_device
  ON public.recovery_settings(user_id, device_id);

CREATE INDEX IF NOT EXISTS idx_recovery_settings_user
  ON public.recovery_settings(user_id);

CREATE TABLE public.recovery_file_listings (
  id uuid NOT NULL DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL,
  device_id uuid NOT NULL,
  file_name text NOT NULL,
  full_path text NOT NULL,
  extension text,
  size_bytes bigint NOT NULL DEFAULT 0,
  last_modified_at timestamp with time zone,
  is_directory boolean NOT NULL DEFAULT false,
  drive_letter text,
  created_at timestamp with time zone DEFAULT now(),
  updated_at timestamp with time zone DEFAULT now(),
  CONSTRAINT recovery_file_listings_pkey PRIMARY KEY (id),
  CONSTRAINT recovery_file_listings_user_id_fkey FOREIGN KEY (user_id) REFERENCES public.app_users(id),
  CONSTRAINT recovery_file_listings_device_id_fkey FOREIGN KEY (device_id) REFERENCES public.devices(id)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_recovery_file_owner_device_path
  ON public.recovery_file_listings(user_id, device_id, full_path);

CREATE INDEX IF NOT EXISTS idx_recovery_file_owner_device
  ON public.recovery_file_listings(user_id, device_id);

CREATE INDEX IF NOT EXISTS idx_recovery_file_last_modified
  ON public.recovery_file_listings(last_modified_at);

-- Suggested production migration SQL for Supabase:
--
-- alter table public.devices
--   alter column last_seen_at type timestamp with time zone
--   using nullif(last_seen_at, '')::timestamp with time zone;
--
-- create unique index if not exists idx_devices_user_hardware
--   on public.devices(user_id, device_id);
--
-- create table if not exists public.recovery_settings (
--   id uuid primary key default gen_random_uuid(),
--   user_id uuid not null references public.app_users(id),
--   device_id uuid not null references public.devices(id),
--   device_name text not null,
--   enabled boolean not null default false,
--   approved_locations jsonb not null default '[]'::jsonb,
--   last_synced_at timestamp with time zone,
--   created_at timestamp with time zone default now(),
--   updated_at timestamp with time zone default now()
-- );
--
-- create unique index if not exists idx_recovery_settings_owner_device
--   on public.recovery_settings(user_id, device_id);
--
-- create table if not exists public.recovery_file_listings (
--   id uuid primary key default gen_random_uuid(),
--   user_id uuid not null references public.app_users(id),
--   device_id uuid not null references public.devices(id),
--   file_name text not null,
--   full_path text not null,
--   extension text,
--   size_bytes bigint not null default 0,
--   last_modified_at timestamp with time zone,
--   is_directory boolean not null default false,
--   drive_letter text,
--   created_at timestamp with time zone default now(),
--   updated_at timestamp with time zone default now()
-- );
--
-- create unique index if not exists idx_recovery_file_owner_device_path
--   on public.recovery_file_listings(user_id, device_id, full_path);
