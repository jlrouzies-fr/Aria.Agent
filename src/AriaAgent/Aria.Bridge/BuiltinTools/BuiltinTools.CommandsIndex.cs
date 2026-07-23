using System.Runtime.InteropServices;
using System.Text.Json;

namespace Aria.Bridge;

public static partial class BuiltinTools
{
    private static IEnumerable<BridgeToolInfo> CommandsIndexToolInfos()
    {
        yield return new("commands_index",
            "Get build, run, test and package-management commands for a language or tool. Call this before running unfamiliar commands.",
            Js("""
               {"type":"object",
                "properties":{
                  "topic": {"type":"string","description":"Language, framework or tool (e.g. rust, python, dotnet, docker, git). Omit for overview."}
                }}
               """));
    }

    private static ToolCallResponse CommandsIndex(Dictionary<string, JsonElement> args)
    {
        var topic = (args.Str("topic") ?? "").ToLowerInvariant().Trim();
        return new ToolCallResponse(BuildCommandsIndex(topic), false);
    }

    private static string BuildCommandsIndex(string topic) => topic switch
    {
        "python" or "py" or "pip" or "uv" or "poetry" or "pytest" => """
            ## Python

            # uv (modern fast tool — preferred)
            uv init my-project && cd my-project
            uv add requests numpy          # add dependencies
            uv run python script.py        # run script
            uv run pytest                  # run tests
            uv run ruff check . --fix      # lint + auto-fix
            uv run black .                 # format
            uv pip install -r requirements.txt

            # pip + venv
            python3 -m venv .venv
            source .venv/bin/activate      # Mac/Linux
            .venv\Scripts\activate         # Windows
            pip install -r requirements.txt
            python -m pytest -v

            # poetry
            poetry install
            poetry run python main.py
            poetry add requests
            poetry build

            # common patterns
            python -m http.server 8080     # quick HTTP server
            python -c "import sys; print(sys.version)"
            """,

        "node" or "nodejs" or "npm" or "javascript" or "js" => """
            ## Node.js / JavaScript

            node script.js
            node --watch script.js         # auto-restart on change

            # npm
            npm install
            npm run dev / build / start / test
            npm install express            # add package
            npm install -D typescript      # dev dependency
            npx create-next-app@latest     # scaffold Next.js

            # npx (run without installing)
            npx http-server .
            npx prettier --write .

            # package.json scripts run via: npm run <name>
            """,

        "typescript" or "ts" or "tsc" or "tsx" => """
            ## TypeScript

            # compile
            npx tsc                        # compile per tsconfig.json
            npx tsc --noEmit               # type-check only (no output)
            npx tsc --watch                # watch mode

            # run directly (no compile step)
            npx tsx src/index.ts           # tsx (fast, ESM)
            npx ts-node src/index.ts       # ts-node (CommonJS)

            # typical dev flow
            npm install
            npm run dev                    # usually wraps tsx/tsc --watch

            # build for production
            npm run build                  # project-specific
            npx tsc && node dist/index.js

            # tsconfig init
            npx tsc --init
            """,

        "rust" or "cargo" => """
            ## Rust / Cargo

            cargo new my-project           # create binary project
            cargo new --lib my-lib         # create library
            cargo build                    # debug build → target/debug/
            cargo build --release          # release build → target/release/
            cargo run                      # build + run
            cargo run -- arg1 arg2         # pass args
            cargo test                     # run all tests
            cargo test my_test_name        # run specific test
            cargo check                    # fast type-check (no binary)
            cargo clippy                   # lint (use -- -D warnings for strict)
            cargo fmt                      # format code
            cargo doc --open               # generate + open docs
            cargo add serde --features derive  # add dependency
            cargo update                   # update Cargo.lock
            cargo clean                    # remove target/

            # Cargo.toml workspace
            cargo build --workspace
            cargo test -p my-crate         # test specific crate
            """,

        "go" or "golang" => """
            ## Go

            go mod init module-name        # create module
            go build ./...                 # build all packages
            go run main.go                 # build + run
            go run .                       # run current package
            go test ./...                  # test all packages
            go test -v -run TestName ./... # verbose, specific test
            go get github.com/pkg/pkg      # add dependency
            go mod tidy                    # sync go.mod + go.sum
            go vet ./...                   # static analysis
            go fmt ./...                   # format
            gofmt -w .                     # format in-place
            golangci-lint run              # comprehensive lint
            go build -o ./bin/app .        # specific output
            """,

        "dotnet" or "csharp" or "c#" or ".net" or "cs" => """
            ## .NET / C#

            dotnet new console -n MyApp    # console app
            dotnet new webapi -n MyApi     # ASP.NET Web API
            dotnet new blazorserver        # Blazor Server
            dotnet new classlib -n MyLib   # class library

            dotnet build                   # build
            dotnet run                     # build + run
            dotnet run --project ./MyApp   # specific project
            dotnet watch run               # hot reload
            dotnet test                    # run tests
            dotnet test --logger "console;verbosity=detailed"
            dotnet publish -c Release -o ./out   # publish
            dotnet publish -c Release -r osx-arm64 --self-contained

            dotnet add package Newtonsoft.Json     # add NuGet package
            dotnet restore                         # restore packages
            dotnet list package                    # list packages
            dotnet format                          # format code

            # solution
            dotnet sln add ./MyProject/MyProject.csproj
            dotnet build MyApp.sln
            """,

        "java" or "maven" or "mvn" or "gradle" => """
            ## Java

            # Maven
            mvn compile                    # compile
            mvn test                       # test
            mvn package                    # build JAR → target/
            mvn package -DskipTests        # skip tests
            mvn spring-boot:run            # Spring Boot dev server
            mvn dependency:tree            # dependency tree
            mvn clean install              # clean + install to local repo

            # Gradle
            ./gradlew build                # build
            ./gradlew test                 # test
            ./gradlew run                  # run (application plugin)
            ./gradlew bootRun              # Spring Boot
            ./gradlew clean                # clean
            ./gradlew dependencies         # show deps
            gradle wrapper                 # create gradlew

            # Run JAR
            java -jar target/my-app.jar
            java -cp target/classes com.example.Main
            """,

        "swift" => """
            ## Swift

            # Swift Package Manager
            swift package init --type executable   # create package
            swift build                            # build
            swift run                              # build + run
            swift test                             # run tests
            swift package update                   # update dependencies

            # Xcode (Mac)
            xcodebuild -scheme MyApp -configuration Release
            xcodebuild test -scheme MyApp -destination 'platform=macOS'

            # Run binary
            .build/debug/MyApp
            .build/release/MyApp
            """,

        "kotlin" => """
            ## Kotlin

            # Gradle (most common)
            ./gradlew build
            ./gradlew run
            ./gradlew test
            ./gradlew jar                  # build JAR

            # kotlinc (compiler)
            kotlinc main.kt -include-runtime -d app.jar
            java -jar app.jar

            # Kotlin script
            kotlinc -script script.kts
            """,

        "ruby" or "rails" or "gem" or "bundler" => """
            ## Ruby

            bundle install                 # install gems (from Gemfile)
            bundle exec ruby script.rb     # run with bundled gems
            bundle exec rake               # run Rake tasks

            # Rails
            rails new my-app
            rails server / rails s         # dev server (localhost:3000)
            rails console / rails c        # interactive console
            rails db:migrate               # run migrations
            rails generate scaffold Post   # scaffold
            rails test                     # run tests
            bundle exec rspec              # RSpec tests

            gem install rails              # install gem globally
            """,

        "php" or "composer" or "laravel" => """
            ## PHP

            php -S localhost:8000          # built-in dev server
            php script.php                 # run script
            php artisan serve              # Laravel dev server
            php artisan make:model Post    # create model
            php artisan migrate            # run migrations
            php artisan test               # run tests

            composer install               # install dependencies
            composer require vendor/pkg    # add package
            composer update                # update packages
            composer dump-autoload         # rebuild autoload
            """,

        "c" or "c++" or "cpp" or "cmake" or "make" => """
            ## C / C++

            # gcc / g++
            gcc -o app main.c
            g++ -o app main.cpp -std=c++17 -Wall -O2
            ./app                          # run

            # CMake
            cmake -B build -DCMAKE_BUILD_TYPE=Release
            cmake --build build
            cmake --build build --target install
            ctest --test-dir build -V      # run tests

            # Make
            make                           # default target
            make clean && make             # clean rebuild
            make install                   # install
            make -j$(nproc)                # parallel build

            # clang
            clang -o app main.c
            clang++ -o app main.cpp -std=c++17
            clang-tidy main.cpp            # lint
            clang-format -i *.cpp *.h      # format in-place
            """,

        "docker" => """
            ## Docker

            # Images
            docker build -t my-image .
            docker build -t my-image:v1.0 -f Dockerfile.prod .
            docker images
            docker rmi my-image

            # Containers
            docker run -it my-image /bin/sh      # interactive
            docker run -d -p 8080:80 my-image    # background, port map
            docker run --rm my-image             # remove on exit
            docker run -v $(pwd):/app my-image   # mount volume
            docker ps                            # running containers
            docker ps -a                         # all containers
            docker stop <id> && docker rm <id>
            docker logs -f <id>                  # follow logs
            docker exec -it <id> /bin/sh         # shell into running container

            # Compose
            docker compose up -d                 # start services
            docker compose down                  # stop + remove
            docker compose build
            docker compose logs -f

            # Registry
            docker push registry/image:tag
            docker pull registry/image:tag
            """,

        "git" => """
            ## Git

            git init && git add . && git commit -m "init"
            git clone <url>
            git status / git diff / git log --oneline

            # Branches
            git checkout -b feature/my-branch
            git branch -d my-branch
            git merge my-branch
            git rebase main

            # Staging
            git add -p                     # interactive hunk staging
            git reset HEAD <file>          # unstage

            # Remote
            git remote add origin <url>
            git push -u origin main
            git pull --rebase              # pull with rebase

            # Stash
            git stash / git stash pop / git stash list

            # Fix last commit
            git commit --amend --no-edit

            # Undo
            git revert HEAD                # new revert commit
            git reset --soft HEAD~1        # undo commit, keep changes staged
            """,

        _ => $"""
            ## Terminal Built-in Tools

            Available tools: bash_exec, read_file, write_file, edit_file, list_dir, glob, grep, git_status, git_diff, git_log, git_stage, git_commit, git_discard, commands_index

            Available build knowledge topics — call commands_index(topic="<name>"):
              python, typescript, rust, go, dotnet, java, swift, kotlin, ruby, php, c++, docker, git

            Platform: {(IsWindows ? "Windows (cmd.exe / PowerShell)" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS (/bin/sh, Homebrew)" : "Linux (/bin/sh, apt/pacman/dnf)")}
            Home directory: {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}

            Tips:
            - Always prefer absolute paths. Use ~ for home directory.
            - Use list_dir or glob to explore before editing; use grep to search file contents.
            - Use read_file before edit_file to verify exact text.
            - Use the git_* tools (not bash_exec git …) for status/diff/log/stage/commit/discard.
            - edit_file requires old_string to appear exactly once.
            - bash_exec returns JSON with fields: exit_code (int), stdout (string), stderr (string).
            """
    };
}
