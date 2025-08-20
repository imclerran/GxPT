# Compatible with Python 3.4.4 for Windows XP support.

import os

def count_sloc_in_file(file_path):
    """Count the number of non-empty, non-comment lines in a C# file."""
    sloc = 0
    with open(file_path, 'r', encoding='utf-8') as file:
        in_block_comment = False

        for line in file:
            stripped_line = line.strip()

            if in_block_comment:
                if '*/' in stripped_line:
                    in_block_comment = False
                continue

            if not stripped_line or stripped_line.startswith('//'):
                continue

            if stripped_line.startswith('/*'):
                in_block_comment = True
                continue

            sloc += 1

    return sloc

def count_sloc_in_directory(directory):
    """Recursively count SLOC in all `.cs` files in the given directory."""
    file_sloc_pairs = []

    for root, _, files in os.walk(directory):
        for file in files:
            if file.endswith('.cs'):
                file_path = os.path.join(root, file)
                sloc = count_sloc_in_file(file_path)
                file_sloc_pairs.append((file_path, sloc))

    return file_sloc_pairs

def print_sloc_table(file_sloc_pairs):
    """Print SLOC information in a formatted table."""
    header = "{:<60} {:>10}".format("Filename", "SLOC")
    print(header)
    print("=" * len(header))

    total_sloc = 0

    for file_path, sloc in file_sloc_pairs:
        print("{:<60} {:>10}".format(file_path, sloc))
        total_sloc += sloc

    print("=" * len(header))
    print("{:<60} {:>10}".format("Total", total_sloc))

if __name__ == "__main__":
    directory_to_scan = input("Enter the directory path to scan: ")
    file_sloc_pairs = count_sloc_in_directory(directory_to_scan)
    
    # Sort by sloc in descending order
    file_sloc_pairs.sort(key=lambda x: x[1], reverse=True)
    
    print_sloc_table(file_sloc_pairs)
