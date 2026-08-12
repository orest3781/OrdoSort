import os
import shutil
from pathlib import Path
from datetime import datetime
import csv
import win32security
import sys
import json
import fitz  # PyMuPDF for PDF handling

# ============================
# Configuration Loading
# ============================

def generate_template_config(config_path):
    """
    Generates a template config.json file with default values.
    """
    template_config = {
        "directories": {
            "source_directory": "",
            "destination_directory": "",
            "archive_directory_base": "",
            "log_directory": "",
            "garbage_directory": "",
            "processed_texts_archive_directory": ""
        },
        "logging": {
            "log_file_name_base": "xxxxxx-MOVE-LOG",
            "log_date_format": "%Y%m%d",
            "log_headers": [
                "DATE-TIME",
                "FILE-OWNER",
                "FILE-NAME",
                "ACTION",
                "PDF-PAGE-COUNT",
                "SOURCE-FOLDER",
                "SOURCE-PATH",
                "DESTINATION-PATH",
                "PDF-FILE-SIZE",
                "LAST-ACCESSED"  # Added LAST-ACCESSED
            ],
            "timestamp_format": "%Y-%m-%d %H:%M:%S"
        },
        "archive": {
            "archive_folder_prefix": "prefix",
            "archive_folder_format": "%Y%m%d"
        },
        "defaults": {
            "default_owner": "default"
        }
    }

    try:
        with open(config_path, 'w', encoding='utf-8') as config_file:
            json.dump(template_config, config_file, indent=4)
        print(f"Template configuration file created at {config_path}. Please review and customize it before running the script again.")
    except Exception as e:
        print(f"Failed to create template configuration file at {config_path}: {e}")
    sys.exit(0)

def validate_config(config):
    """
    Validates the loaded configuration to ensure required fields are present.
    """
    expected_headers = [
        "DATE-TIME",
        "FILE-OWNER",
        "FILE-NAME",
        "ACTION",
        "PDF-PAGE-COUNT",
        "SOURCE-FOLDER",
        "SOURCE-PATH",
        "DESTINATION-PATH",
        "PDF-FILE-SIZE",
        "LAST-ACCESSED"  # Ensure LAST-ACCESSED is present
    ]

    log_headers = config.get("logging", {}).get("log_headers", [])
    if log_headers != expected_headers:
        raise ValueError(f"log_headers in config.json do not match expected headers:\nExpected: {expected_headers}\nFound: {log_headers}")

def load_config(config_path):
    """
    Load the JSON configuration from the given path.
    If config.json does not exist, generate a template and exit.
    """
    if not config_path.exists():
        print(f"Configuration file not found at {config_path}. Generating a template config.json.")
        generate_template_config(config_path)

    try:
        with open(config_path, 'r', encoding='utf-8') as config_file:
            config = json.load(config_file)
        
        # Validate log_headers
        validate_config(config)
        
        return config
    except json.JSONDecodeError as e:
        print(f"Error decoding JSON from the configuration file: {e}")
        log_error_event(f"JSON Decode Error: {e}")
        sys.exit(1)
    except ValueError as ve:
        print(f"Configuration Validation Error: {ve}")
        log_error_event(f"Configuration Validation Error: {ve}")
        sys.exit(1)
    except Exception as e:
        print(f"Unexpected error loading configuration: {e}")
        log_error_event(f"Unexpected Config Load Error: {e}")
        sys.exit(1)

# Determine the path to the config.json relative to the script/executable
if getattr(sys, 'frozen', False):
    # If the application is run as a bundle (PyInstaller)
    base_path = Path(sys.executable).parent
else:
    # If run as a script
    base_path = Path(__file__).parent

CONFIG_PATH = base_path / 'config.json'

# ============================
# Setup Error Logging
# ============================

def log_error_event(error_message):
    """
    Logs error messages to a separate error log file in the root directory.
    """
    error_log_path = base_path / "error_log.txt"
    timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    log_entry = f"{timestamp} - ERROR: {error_message}\n"
    try:
        with open(error_log_path, 'a', encoding='utf-8') as error_log_file:
            error_log_file.write(log_entry)
    except Exception as e:
        print(f"Failed to write to error log file {error_log_path}: {e}")

# Load the configuration (this will generate template if not found)
config = load_config(CONFIG_PATH)

# Extract configurations
DIRECTORIES = config.get("directories", {})
LOGGING_CONFIG = config.get("logging", {})
ARCHIVE_CONFIG = config.get("archive", {})
DEFAULTS = config.get("defaults", {})

SOURCE_DIRECTORY = Path(DIRECTORIES.get("source_directory"))
DESTINATION_DIRECTORY = Path(DIRECTORIES.get("destination_directory"))
ARCHIVE_DIRECTORY_BASE = Path(DIRECTORIES.get("archive_directory_base"))
LOG_DIRECTORY = Path(DIRECTORIES.get("log_directory"))
GARBAGE_DIRECTORY = Path(DIRECTORIES.get("garbage_directory"))
PROCESSED_TEXTS_ARCHIVE_DIRECTORY = Path(DIRECTORIES.get("processed_texts_archive_directory"))

LOG_FILE_NAME_BASE = LOGGING_CONFIG.get("log_file_name_base", "PAPER-MOVE-LOG")
LOG_DATE_FORMAT = LOGGING_CONFIG.get("log_date_format", "%Y%m%d")
LOG_HEADERS = LOGGING_CONFIG.get("log_headers", [
    "DATE-TIME",
    "FILE-OWNER",
    "FILE-NAME",
    "ACTION",
    "PDF-PAGE-COUNT",
    "SOURCE-FOLDER",
    "SOURCE-PATH",
    "DESTINATION-PATH",
    "PDF-FILE-SIZE",
    "LAST-ACCESSED"  # Ensure this is included
])
TIMESTAMP_FORMAT = LOGGING_CONFIG.get("timestamp_format", "%Y-%m-%d %H:%M:%S")

ARCHIVE_FOLDER_PREFIX = ARCHIVE_CONFIG.get("archive_folder_prefix", "PAPER")
ARCHIVE_FOLDER_FORMAT = ARCHIVE_CONFIG.get("archive_folder_format", "%Y%m%d")

DEFAULT_OWNER = DEFAULTS.get("default_owner", "PAPERVISION")

# ============================
# Setup Logging
# ============================

def get_log_file_path():
    """
    Determine the current log file path based on the date.
    """
    log_date = datetime.now().strftime(LOG_DATE_FORMAT)
    log_file_name = f"{log_date}-{LOG_FILE_NAME_BASE}.csv"
    return LOG_DIRECTORY / log_file_name

def setup_logging():
    """
    Create the log file with headers if it doesn't exist.
    """
    log_path = get_log_file_path()
    if not log_path.exists():
        try:
            with open(log_path, 'w', newline='', encoding='utf-8') as log_file:
                writer = csv.writer(log_file)
                writer.writerow(LOG_HEADERS)
        except Exception as e:
            print(f"Failed to create log file {log_path}: {e}")
            log_error_event(f"Failed to create log file {log_path}: {e}")
    return log_path

# ============================
# Helper Functions
# ============================

def log_move_event(file_owner, file_name, file_source, file_destination, pdf_page_count, pdf_file_size, last_accessed):
    """
    Logs move operations to the CSV log file, including PDF page count, file size, and last accessed time.
    """
    if all([file_name != "N/A", file_source != "N/A", file_destination != "N/A"]):
        timestamp = datetime.now().strftime(TIMESTAMP_FORMAT)
        log_path = setup_logging()
        
        source_path = Path(file_source)
        destination_path = Path(file_destination)
        
        # **Updated Code: Include parent folder in SOURCE-FOLDER**
        if len(source_path.parents) >= 2:
            source_folder = f"{source_path.parents[1].name}/{source_path.parents[0].name}"
        else:
            source_folder = source_path.parent.name
        # **End of Update**
        
        source_dir = source_path.parent
        dest_dir = destination_path.parent
        
        log_entry = [
            timestamp,                 # DATE-TIME
            file_owner,               # FILE-OWNER
            file_name,                # FILE-NAME
            "Move",                   # ACTION
            pdf_page_count,           # PDF-PAGE-COUNT
            source_folder,            # SOURCE-FOLDER (Updated)
            str(source_dir),          # SOURCE-PATH
            str(dest_dir),            # DESTINATION-PATH
            pdf_file_size,            # PDF-FILE-SIZE
            last_accessed             # LAST-ACCESSED (Added)
        ]
        try:
            with open(log_path, 'a', newline='', encoding='utf-8') as log_file:
                writer = csv.writer(log_file)
                writer.writerow(log_entry)
        except Exception as e:
            print(f"Failed to write to log file {log_path}: {e}")
            log_error_event(f"Failed to write to log file {log_path}: {e}")

def get_file_owner(file_path):
    """
    Retrieves the owner of the file using Windows security.
    Returns DEFAULT_OWNER if retrieval fails.
    """
    if not file_path.exists():
        # Since we're only logging moves, we skip logging here
        return DEFAULT_OWNER
    try:
        sd = win32security.GetFileSecurity(str(file_path), win32security.OWNER_SECURITY_INFORMATION)
        owner_sid = sd.GetSecurityDescriptorOwner()
        name, domain, _ = win32security.LookupAccountSid(None, owner_sid)
        return f"{domain}\\{name}"
    except Exception as e:
        # Since we're only logging moves, we skip logging here
        log_error_event(f"Failed to get file owner for {file_path}: {e}")
        return DEFAULT_OWNER

def get_unique_file_path(path):
    """
    Generates a unique file path to prevent overwriting existing files.
    """
    path = Path(path)
    if not path.exists():
        return path
    counter = 2
    while path.exists():
        path = path.with_name(f"{path.stem} ({counter}){path.suffix}")
        counter += 1
    return path

def get_unique_directory_path(path):
    """
    Generates a unique directory path to prevent overwriting existing directories.
    """
    path = Path(path)
    counter = 1
    while path.exists():
        path = Path(f"{path.parent}/{path.stem} ({counter})")
        counter += 1
    return path

def ensure_directory_exists(path):
    """
    Creates a directory if it doesn't exist.
    """
    path = Path(path)
    if not path.exists():
        try:
            path.mkdir(parents=True, exist_ok=True)
            # Since we're only logging moves, we skip logging directory creation
        except Exception as e:
            # Since we're only logging moves, we skip logging errors
            print(f"Failed to create directory {path}: {e}")
            log_error_event(f"Failed to create directory {path}: {e}")
            raise e

def get_daily_archive_folder():
    """
    Creates a unique daily archive folder based on the current date.
    """
    date_string = datetime.now().strftime(ARCHIVE_FOLDER_FORMAT)
    archive_folder_name = f"{date_string}_{ARCHIVE_FOLDER_PREFIX}"
    return ARCHIVE_DIRECTORY_BASE / archive_folder_name

def move_text_file(text_file, archive_dir):
    """
    Moves the processed text file to the archive directory.
    """
    archive_path = archive_dir / text_file.name
    archive_path = get_unique_file_path(archive_path)
    try:
        shutil.move(str(text_file), str(archive_path))
        print(f"Moved text file {text_file} to archive {archive_path}.")
    except Exception as e:
        print(f"Failed to move text file {text_file} to archive {archive_path}: {e}")
        log_error_event(f"Failed to move text file {text_file} to archive {archive_path}: {e}")

def move_file(source_file, destination_dir):
    """
    Moves a file to the destination directory and logs the move.
    Also retrieves PDF page count, file size, and last accessed time for logging.
    """
    # Capture LAST-ACCESSED time before moving
    try:
        last_access_time_epoch = source_file.stat().st_atime
        last_accessed = datetime.fromtimestamp(last_access_time_epoch).strftime(TIMESTAMP_FORMAT)
    except Exception as e:
        print(f"Failed to get last accessed time for {source_file}: {e}")
        log_error_event(f"Failed to get last accessed time for {source_file}: {e}")
        last_accessed = "Unknown"

    destination_path = destination_dir / source_file.name
    destination_path = get_unique_file_path(destination_path)

    try:
        shutil.move(str(source_file), str(destination_path))
    except Exception as e:
        # Since we're only logging moves, we skip logging move failures
        print(f"Failed to move {source_file} to {destination_path}: {e}")
        log_error_event(f"Failed to move {source_file} to {destination_path}: {e}")
        return None

    # Retrieve PDF page count using fitz (PyMuPDF)
    try:
        with fitz.open(str(destination_path)) as doc:
            pdf_page_count = len(doc)
    except Exception as e:
        print(f"Failed to read PDF file {destination_path}: {e}")
        log_error_event(f"Failed to read PDF file {destination_path}: {e}")
        pdf_page_count = "N/A"

    # Retrieve PDF file size in bytes
    try:
        pdf_file_size = destination_path.stat().st_size
    except Exception as e:
        print(f"Failed to get file size for {destination_path}: {e}")
        log_error_event(f"Failed to get file size for {destination_path}: {e}")
        pdf_file_size = "N/A"

    # Get file owner
    owner = get_file_owner(destination_path)

    # Log the move event with additional PDF info
    log_move_event(
        file_owner=owner,
        file_name=destination_path.name,
        file_source=str(source_file),
        file_destination=str(destination_path),
        pdf_page_count=pdf_page_count,
        pdf_file_size=pdf_file_size,
        last_accessed=last_accessed  # Pass the last accessed time
    )

    return destination_path

def get_file_name_from_path(file_path):
    """
    Extracts the file name from a given path.
    """
    return Path(file_path).name

# ============================
# Main Processing
# ============================

def main():
    """
    Main function to execute the file moving and logging operations.
    """
    # Ensure base directories exist
    base_directories = [LOG_DIRECTORY, GARBAGE_DIRECTORY, ARCHIVE_DIRECTORY_BASE, PROCESSED_TEXTS_ARCHIVE_DIRECTORY]
    for dir_path in base_directories:
        ensure_directory_exists(dir_path)

    # Initialize archive directory
    archive_directory = get_daily_archive_folder()
    ensure_directory_exists(archive_directory)

    # Fetch PDF files
    try:
        pdf_files = list(SOURCE_DIRECTORY.rglob("*.pdf"))
    except Exception as e:
        print(f"Failed to retrieve PDF files from {SOURCE_DIRECTORY}: {e}")
        log_error_event(f"Failed to retrieve PDF files from {SOURCE_DIRECTORY}: {e}")
        sys.exit(1)

    processed_pdfs = {}
    processed_count = 0

    # Process text files
    try:
        text_files = list(SOURCE_DIRECTORY.glob("*.txt"))
    except Exception as e:
        print(f"Failed to retrieve text files from {SOURCE_DIRECTORY}: {e}")
        log_error_event(f"Failed to retrieve text files from {SOURCE_DIRECTORY}: {e}")
        sys.exit(1)

    for text_file in text_files:
        try:
            with open(text_file, 'r', encoding='utf-8') as f:
                content_lines = f.readlines()
        except Exception as e:
            print(f"Failed to read text file {text_file}: {e}")
            log_error_event(f"Failed to read text file {text_file}: {e}")
            continue

        for line in content_lines:
            data_pieces = [piece.strip() for piece in line.strip().split(',') if piece.strip()]
            best_match_count = 0
            best_matches = []

            for pdf_file in pdf_files:
                if pdf_file.name in processed_pdfs:
                    continue

                # Count how many data pieces match parts of the PDF file name
                match_count = sum(1 for piece in data_pieces if piece.lower() in pdf_file.name.lower())
                if match_count >= 3:
                    if match_count > best_match_count:
                        best_match_count = match_count
                        best_matches = [pdf_file]
                    elif match_count == best_match_count:
                        best_matches.append(pdf_file)

            for match in best_matches:
                moved_path = move_file(match, DESTINATION_DIRECTORY)
                if moved_path:
                    # Optionally, copy to archive without logging
                    archive_path = archive_directory / moved_path.name
                    archive_path = get_unique_file_path(archive_path)
                    try:
                        shutil.copy2(str(moved_path), str(archive_path))
                        # No logging for copy operations
                    except Exception as e:
                        print(f"Failed to copy {moved_path} to archive {archive_path}: {e}")
                        log_error_event(f"Failed to copy {moved_path} to archive {archive_path}: {e}")
                    processed_pdfs[match.name] = True
                    processed_count += 1

        # After processing all lines in the text file, move it to the processed texts archive
        move_text_file(text_file, PROCESSED_TEXTS_ARCHIVE_DIRECTORY)

    if processed_count == 0:
        print("No PDF files were moved.")
    else:
        print(f"{processed_count} PDF file(s) were moved and logged.")

    # Handle folders (optional: moving to garbage without logging)
    try:
        folders = list(SOURCE_DIRECTORY.iterdir())
        folders = [f for f in folders if f.is_dir()]
    except Exception as e:
        print(f"Failed to retrieve folders from {SOURCE_DIRECTORY}: {e}")
        log_error_event(f"Failed to retrieve folders from {SOURCE_DIRECTORY}: {e}")
        sys.exit(1)

    for folder in folders:
        try:
            files = list(folder.iterdir())
        except Exception as e:
            print(f"Failed to retrieve files in folder {folder}: {e}")
            log_error_event(f"Failed to retrieve files in folder {folder}: {e}")
            continue

        move_to_garbage = False

        if len(files) == 0:
            move_to_garbage = True
        elif len(files) == 1 and files[0].name.lower() == "persist.xml":
            move_to_garbage = True

        if move_to_garbage:
            garbage_path = GARBAGE_DIRECTORY / folder.name
            garbage_path = get_unique_directory_path(garbage_path)
            try:
                shutil.move(str(folder), str(garbage_path))
                # No logging for moving to garbage
            except Exception as e:
                print(f"Failed to move folder {folder} to garbage {garbage_path}: {e}")
                log_error_event(f"Failed to move folder {folder} to garbage {garbage_path}: {e}")

    print("Process Complete.")

if __name__ == "__main__":
    main()
